using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintStreamer.Interfaces;

namespace PrintStreamer.Streamers
{
    /// <summary>
    /// Combines video and audio streams into a single H.264 + AAC stream.
    /// Reads from /stream/overlay (video) and /stream/audio (audio).
    /// Outputs MP4 format suitable for RTMP or local recording.
    /// </summary>
    internal sealed class MixStreamer : IStreamer
    {
        private readonly IConfiguration _config;
        private readonly HttpContext _ctx;
        private readonly ILogger<MixStreamer> _logger;
        private Process? _proc;
        private TaskCompletionSource<object?> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExitTask => _exitTcs.Task;

        public MixStreamer(IConfiguration config, HttpContext ctx, ILogger<MixStreamer> logger)
        {
            _config = config;
            _ctx = ctx;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            const string contextLabel = "Video+Audio Mix";

            try
            {
                var overlaySource = "http://127.0.0.1:8080/stream/overlay";
                var audioSource = "http://127.0.0.1:8080/stream/audio";
                var audioEnabled = _config.GetValue<bool?>("Audio:Enabled") ?? true;

                _logger.LogInformation("[{ContextLabel}] Starting mix: video={Video} audio={Audio}",
                    contextLabel, overlaySource, audioEnabled ? audioSource : "disabled");

                // Set HTTP response headers
                if (!_ctx.Response.HasStarted)
                {
                    _ctx.Response.StatusCode = 200;
                    _ctx.Response.ContentType = "video/mp4";
                    _ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                    _ctx.Response.Headers["Pragma"] = "no-cache";
                }

                // Build ffmpeg command
                var args = BuildFfmpegArgs(overlaySource, audioSource, audioEnabled);

                _logger.LogInformation("[{ContextLabel}] Starting ffmpeg with video and {AudioMode}", contextLabel, audioEnabled ? "audio" : "no audio");

                _proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                using var reg = cancellationToken.Register(() => { try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { } });

                _proc.Start();

                // Log stderr in background
                _ = Task.Run(async () =>
                {
                    var buf = new char[1024];
                    try
                    {
                        while (!_proc.HasExited)
                        {
                            var n = await _proc.StandardError.ReadAsync(buf, 0, buf.Length);
                            if (n > 0)
                            {
                                var s = new string(buf, 0, n).Trim();
                                if (!string.IsNullOrWhiteSpace(s))
                                {
                                    _logger.LogWarning("[{ContextLabel}] [ffmpeg] {Output}", contextLabel, s);
                                }
                            }
                        }
                    }
                    catch { }
                });

                // Stream ffmpeg output to HTTP response
                await _proc.StandardOutput.BaseStream.CopyToAsync(_ctx.Response.Body, 64 * 1024, cancellationToken);

                try { await _ctx.Response.Body.FlushAsync(cancellationToken); } catch { }

                _exitTcs.TrySetResult(null);
                _logger.LogInformation("[{ContextLabel}] Mix stream ended normally", contextLabel);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[{ContextLabel}] Request cancelled", contextLabel);
                _exitTcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ContextLabel}] Error: {Message}", contextLabel, ex.Message);
                if (!_ctx.Response.HasStarted)
                {
                    try
                    {
                        _ctx.Response.StatusCode = 502;
                        await _ctx.Response.WriteAsync("Mix streamer error", cancellationToken);
                    }
                    catch (InvalidOperationException)
                    {
                        _logger.LogWarning("[{ContextLabel}] Could not set error status (response already started)", contextLabel);
                    }
                }
                _exitTcs.TrySetException(ex);
            }
            finally
            {
                try { _proc?.Dispose(); } catch { }
            }
        }

        public void Stop()
        {
            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    _logger.LogInformation("[Video+Audio Mix] Stopping ffmpeg...");
                    try
                    {
                        if (_proc.StartInfo.RedirectStandardInput)
                        {
                            _proc.StandardInput.WriteLine("q");
                            _proc.StandardInput.Flush();
                        }
                    }
                    catch { }

                    if (!_proc.WaitForExit(5000))
                    {
                        _logger.LogWarning("[Video+Audio Mix] ffmpeg did not exit gracefully, killing...");
                        _proc.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Video+Audio Mix] Error stopping ffmpeg");
            }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            try { _proc?.Dispose(); } catch { }
        }

        private static string BuildFfmpegArgs(string videoSource, string audioSource, bool audioEnabled)
        {
            var args = new List<string>
            {
                "-hide_banner",
                "-nostats",
                "-loglevel error",
                "-nostdin",
                "-fflags nobuffer",

                // Video input with reconnect options for HTTP stream resilience
                "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2",
                "-fflags +genpts+discardcorrupt",
                "-analyzeduration 5M -probesize 10M",
                $"-i \"{videoSource}\""
            };

            // Audio input (if enabled)
            if (audioEnabled)
            {
                args.Add("-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2");
                args.Add("-fflags +genpts+discardcorrupt");
                args.Add($"-i \"{audioSource}\"");
            }

            // Video encoding - H.264 with ultrafast preset for low latency
            args.Add("-c:v libx264");
            args.Add("-preset ultrafast");
            args.Add("-b:v 2500k");
            args.Add("-maxrate 3000k");
            args.Add("-bufsize 6000k");
            args.Add("-pix_fmt yuv420p");
            args.Add("-g 60");  // GOP size (keyframe interval)

            // Audio encoding (if enabled) - AAC
            if (audioEnabled)
            {
                args.Add("-c:a aac");
                args.Add("-b:a 128k");
                args.Add("-ar 44100");
            }
            else
            {
                args.Add("-an");  // No audio
            }

            // Output format - MP4 with fragmentation for HTTP streaming
            args.Add("-f mp4");
            args.Add("-movflags +frag_keyframe+empty_moov");
            args.Add("pipe:1");

            return string.Join(" ", args);
        }
    }
}
