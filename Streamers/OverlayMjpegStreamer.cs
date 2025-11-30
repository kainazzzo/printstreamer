using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PrintStreamer.Interfaces;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Streamers
{
    /// <summary>
    /// Per-request MJPEG overlay streamer that mirrors the ffmpeg filter style used in FfmpegStreamer
    /// (drawbox background + drawtext with expansion=none), but writes multipart MJPEG to the HTTP response.
    /// </summary>
    public sealed class OverlayMjpegStreamer : IStreamer
    {
        private readonly IConfiguration _config;
        private readonly Overlay.OverlayTextService _overlayText;
        private readonly HttpContext _ctx;
        private readonly ILogger<OverlayMjpegStreamer> _logger;
        private Process? _proc;
        private TaskCompletionSource<object?> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OverlayMjpegStreamer(IConfiguration config, Overlay.OverlayTextService overlayText, HttpContext ctx, ILogger<OverlayMjpegStreamer> logger)
        {
            _config = config;
            _overlayText = overlayText;
            _ctx = ctx;
            _logger = logger;
        }

        public Task ExitTask => _exitTcs.Task;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            const string contextLabel = "Overlay MJPEG Compositing";
            
            try { _overlayText.Start(); } catch { }

            // Use local /stream/source endpoint instead of raw camera URL
            // This allows the data flow pipeline to work correctly with stage isolation
            var source = _config.GetValue<string>("Overlay:StreamSource");
            if (string.IsNullOrWhiteSpace(source))
            {
                // Fall back to the global Stream:Source config (legacy behavior)
                source = _config.GetValue<string>("Stream:Source") ?? "http://127.0.0.1:8080/stream/source";
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                _logger.LogError("[{ContextLabel}] Stream source not configured", contextLabel);
                if (!_ctx.Response.HasStarted)
                {
                    _ctx.Response.StatusCode = 500;
                    await _ctx.Response.WriteAsync("Stream source not configured", cancellationToken);
                }
                _exitTcs.TrySetResult(null);
                return;
            }

            _logger.LogInformation("[{ContextLabel}] Using video source: {Source}", contextLabel, source);

            // HTTP response headers
            if (!_ctx.Response.HasStarted)
            {
                _ctx.Response.StatusCode = 200;
                _ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                _ctx.Response.Headers["Pragma"] = "no-cache";
            }
            const string boundary = "frame";
            _ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";

            // Overlay configuration mirrors FfmpegStreamer defaults/behavior
            var fontFile = _config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf";
            var fontSize = _config.GetValue<int?>("Overlay:FontSize") ?? 16;
            var textFile = _overlayText.TextFilePath;
            var fontColor = _config.GetValue<string>("Overlay:FontColor") ?? "white";
            var boxColor = _config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4";
            var boxBorderW = _config.GetValue<int?>("Overlay:BoxBorderW") ?? 8;
            // MJPEG output quality (lower is better quality). Clamp to a reasonable range for balance
            var mjpegQ = _config.GetValue<int?>("Overlay:Quality") ?? 5; // 1(best)-31(worst) but we keep 2..10 for balance
            if (mjpegQ < 2) mjpegQ = 2; if (mjpegQ > 10) mjpegQ = 10;
            // Build filter chain: upscale/pad to 1080p first for sharp text, then draw overlays
            var filters = new List<string>();
            // filters.Add("scale=1920:1080:flags=lanczos:force_original_aspect_ratio=decrease");
            // filters.Add("pad=1920:1080:(ow-iw)/2:(oh-ih)/2:black");
            filters.Add("format=yuv420p");

            var boxHeightConfig = _config.GetValue<int?>("Overlay:BoxHeight") ?? 75;
            // X/Y from config (optional). We'll override to place inside the banner when not supplied.
            var layout = OverlayLayout.Calculate(_config, textFile, fontSize, boxHeightConfig);
            // Use OverlayFilterUtil for escaping and drawbox building
            var bannerFraction = OverlayFilterUtil.ClampBannerFraction(_config.GetValue<double?>("Overlay:BannerFraction") ?? 0.2);
            var drawbox = OverlayFilterUtil.BuildDrawbox(layout.DrawboxX, layout.DrawboxY, boxHeightConfig, boxColor);
            filters.Add(drawbox);

            var draw = $"drawtext=fontfile='{OverlayFilterUtil.Esc(fontFile)}':textfile='{OverlayFilterUtil.Esc(textFile)}':reload=1:expansion=none:fontsize={fontSize}:fontcolor={fontColor}:x={layout.TextX}:y={layout.TextY}";

            filters.Add(draw);

var vf = string.Join(",", filters);
_logger.LogInformation("[{ContextLabel}] FFmpeg vf: {Vf}", contextLabel, vf);

            // Input args for HTTP MJPEG source
            var inputArgs = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? $"-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2 -reconnect_on_network_error 1 -fflags +genpts+discardcorrupt -analyzeduration 5M -probesize 10M -max_delay 5000000 -f mjpeg -use_wallclock_as_timestamps 1 -i \"{source}\""
                : $"-i \"{source}\"";

            var args = string.Join(" ", new[]
            {
                "-hide_banner -nostats -loglevel error -nostdin -fflags nobuffer",
                inputArgs,
                "-vf",
                $"\"{vf}\"",
                "-an",
                $"-c:v mjpeg -huffman optimal -q:v {mjpegQ}",
                $"-f mpjpeg -boundary_tag {boundary} pipe:1"
            });

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

            using var reg = _ctx.RequestAborted.Register(() => { try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { } });

            try
            {
                _proc.Start();
                var copyTask = _proc.StandardOutput.BaseStream.CopyToAsync(_ctx.Response.Body, 64 * 1024, _ctx.RequestAborted);
                // Monitor stderr for critical errors (suppress benign warnings)
                var errorCount = 0;
                var lastErrorTime = DateTime.UtcNow;
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
                                    // Suppress repetitive "unable to decode APP fields" errors which are common
                                    // with MJPEG streams and don't indicate fatal problems
                                    if (s.Contains("unable to decode APP fields", StringComparison.OrdinalIgnoreCase) || 
                                        s.Contains("Last message repeated", StringComparison.OrdinalIgnoreCase))
                                    {
                                        // Only log these occasionally to avoid spam
                                        var now = DateTime.UtcNow;
                                        if ((now - lastErrorTime).TotalSeconds > 10)
                                        {
                                            _logger.LogInformation("[{ContextLabel}] Suppressing benign decode warnings (last 10s)", contextLabel);
                                            lastErrorTime = now;
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogWarning("[{ContextLabel}] [ffmpeg stderr] {Output}", contextLabel, s);
                                        errorCount++;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                });
                await copyTask;
                try { await _ctx.Response.Body.FlushAsync(_ctx.RequestAborted); } catch { }
                _exitTcs.TrySetResult(null);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[{ContextLabel}] Request cancelled", contextLabel);
                _exitTcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ContextLabel}] Pipeline error: {Message}", contextLabel, ex.Message);
                if (!_ctx.Response.HasStarted)
                {
                    try
                    {
                        _ctx.Response.StatusCode = 502;
                        await _ctx.Response.WriteAsync($"Overlay pipeline error: {ex.Message}", cancellationToken);
                    }
                    catch (InvalidOperationException)
                    {
                        // Response headers already sent, can't change status code
                        _logger.LogWarning("[{ContextLabel}] Could not set error status (response already started)", contextLabel);
                    }
                }
                _exitTcs.TrySetException(ex);
            }
        }

        public void Stop()
        {
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            try { _proc?.Dispose(); } catch { }
        }
    }
}
