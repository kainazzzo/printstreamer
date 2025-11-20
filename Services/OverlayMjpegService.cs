using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Services
{
    /// <summary>
    /// Service that handles per-request MJPEG overlay streaming.
    /// This mirrors the behavior previously implemented in Streamers/OverlayMjpegStreamer
    /// but is registered as a DI service and exposes a HandleRequestAsync method.
    /// </summary>
    public sealed class OverlayMjpegService
    {
        private readonly IConfiguration _config;
        private readonly Overlay.OverlayTextService _overlayText;
        private readonly ILogger<OverlayMjpegService> _logger;

        public OverlayMjpegService(IConfiguration config, Overlay.OverlayTextService overlayText, ILogger<OverlayMjpegService> logger)
        {
            _config = config;
            _overlayText = overlayText;
            _logger = logger;
        }

        public async Task HandleRequestAsync(HttpContext ctx, CancellationToken cancellationToken = default)
        {
            try { _overlayText.Start(); } catch { }

            var source = _config.GetValue<string>("Stream:Source");
            if (string.IsNullOrWhiteSpace(source))
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync("Stream source not configured", cancellationToken);
                return;
            }

            // HTTP response headers
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            ctx.Response.Headers["Pragma"] = "no-cache";
            const string boundary = "frame";
            ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";

            // Overlay configuration mirrors FfmpegStreamer defaults/behavior
            var fontFile = _config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf";
            var fontSize = _config.GetValue<int?>("Overlay:FontSize") ?? 16;
            var textFile = _overlayText.TextFilePath;
            var fontColor = _config.GetValue<string>("Overlay:FontColor") ?? "white";
            var boxColor = _config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4";
            var boxBorderW = _config.GetValue<int?>("Overlay:BoxBorderW") ?? 8;
            var xConfig = _config.GetValue<string>("Overlay:X");
            var yConfig = _config.GetValue<string>("Overlay:Y");
            var x = "0";
            var y = yConfig;

            string esc(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
            var filters = new List<string>();
            filters.Add("format=yuv420p");

            var bannerFraction = _config.GetValue<double?>("Overlay:BannerFraction") ?? 0.2;
            if (bannerFraction < 0) bannerFraction = 0; if (bannerFraction > 0.6) bannerFraction = 0.6;
            var bf = bannerFraction.ToString(CultureInfo.InvariantCulture);
            var drawbox = $"drawbox=x=0:y=ih*(1-{bf}):w=iw:h=ih*{bf}:color={boxColor}:t=fill";
            filters.Add(drawbox);

            int lineCount = 1;
            try
            {
                var initialText = System.IO.File.Exists(_overlayText.TextFilePath) ? System.IO.File.ReadAllText(_overlayText.TextFilePath) : string.Empty;
                if (!string.IsNullOrEmpty(initialText)) lineCount = initialText.Split('\n').Length;
            }
            catch { }
            var approxTextHeight = Math.Max(fontSize, 12) * Math.Max(1, lineCount);
            var padding = 32;
            var extra = 6;
            var boxH = padding + approxTextHeight + boxBorderW + extra;
            var textY = $"h-({boxH})+{padding / 2}";
            var textX = $"{x} + {padding / 2}";
            var draw = $"drawtext=fontfile='{esc(fontFile)}':textfile='{esc(textFile)}':reload=1:expansion=none:fontsize={fontSize}:fontcolor={fontColor}:x={textX}:y={textY}";

            filters.Add(draw);

            var vf = string.Join(",", filters);

            var inputArgs = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? $"-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2 -fflags +genpts -f mjpeg -use_wallclock_as_timestamps 1 -i \"{source}\""
                : $"-i \"{source}\"";

            var args = string.Join(" ", new[]
            {
                "-hide_banner -nostats -loglevel error -nostdin",
                inputArgs,
                "-vf",
                $"\"{vf}\"",
                "-an",
                $"-f mpjpeg -boundary_tag {boundary} -q:v 5 pipe:1"
            });

            var proc = new Process
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

            using var reg = ctx.RequestAborted.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });

            try
            {
                proc.Start();
                
                // Monitor stderr for critical errors (suppress benign warnings)
                var errorCount = 0;
                var lastErrorTime = DateTime.UtcNow;
                _ = Task.Run(async () =>
                {
                    var buf = new char[1024];
                    try
                    {
                        while (!proc.HasExited)
                        {
                            var n = await proc.StandardError.ReadAsync(buf, 0, buf.Length);
                            if (n > 0)
                            {
                                var s = new string(buf, 0, n).Trim();
                                if (!string.IsNullOrWhiteSpace(s))
                                {
                                    // Suppress repetitive "unable to decode APP fields" errors which are common
                                    // with MJPEG streams and don't indicate fatal problems
                                    if (s.Contains("unable to decode APP fields") || 
                                        s.Contains("Last message repeated"))
                                    {
                                        // Only log these occasionally to avoid spam
                                        var now = DateTime.UtcNow;
                                            if ((now - lastErrorTime).TotalSeconds > 10)
                                        {
                                            _logger.LogInformation("[OverlayMJPEG] Suppressing benign decode warnings (last 10s)");
                                            lastErrorTime = now;
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogWarning("[OverlayMJPEG ffmpeg] {Message}", s);
                                        errorCount++;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                });
                
                // Copy output with error recovery - if copy fails, the client disconnected
                await proc.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, 64 * 1024, ctx.RequestAborted);
                try { await ctx.Response.Body.FlushAsync(ctx.RequestAborted); } catch { }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected or request cancelled - this is normal
            }
            catch (Exception ex)
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 502;
                    await ctx.Response.WriteAsync($"Overlay pipeline error: {ex.Message}", cancellationToken);
                }
                else
                {
                    // Response already started, log the error but don't try to write to response
                    _logger.LogError(ex, "[OverlayMJPEG] Pipeline error after response started");
                }
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                try { proc?.Dispose(); } catch { }
            }
        }
    }
}
