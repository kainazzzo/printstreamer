using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PrintStreamer.Interfaces;

namespace PrintStreamer.Streamers
{
    /// <summary>
    /// Per-request MJPEG overlay streamer that mirrors the ffmpeg filter style used in FfmpegStreamer
    /// (drawbox background + drawtext with expansion=none), but writes multipart MJPEG to the HTTP response.
    /// </summary>
    internal sealed class OverlayMjpegStreamer : IStreamer
    {
        private readonly IConfiguration _config;
        private readonly Overlay.OverlayTextService _overlayText;
        private readonly HttpContext _ctx;
        private Process? _proc;
        private TaskCompletionSource<object?> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OverlayMjpegStreamer(IConfiguration config, Overlay.OverlayTextService overlayText, HttpContext ctx)
        {
            _config = config;
            _overlayText = overlayText;
            _ctx = ctx;
        }

        public Task ExitTask => _exitTcs.Task;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            try { _overlayText.Start(); } catch { }

            var source = _config.GetValue<string>("Stream:Source");
            if (string.IsNullOrWhiteSpace(source))
            {
                _ctx.Response.StatusCode = 500;
                await _ctx.Response.WriteAsync("Stream source not configured", cancellationToken);
                _exitTcs.TrySetResult(null);
                return;
            }

            // HTTP response headers
            _ctx.Response.StatusCode = 200;
            _ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            _ctx.Response.Headers["Pragma"] = "no-cache";
            const string boundary = "frame";
            _ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";

            // Overlay configuration mirrors FfmpegStreamer defaults/behavior
            var fontFile = _config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf";
            var fontSize = _config.GetValue<int?>("Overlay:FontSize") ?? 16;
            var textFile = _overlayText.TextFilePath;
            var fontColor = _config.GetValue<string>("Overlay:FontColor") ?? "white";
            var boxColor = _config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4";
            var boxBorderW = _config.GetValue<int?>("Overlay:BoxBorderW") ?? 8;
            // X/Y from config (optional). We'll override to place inside the banner when not supplied.
            var xConfig = _config.GetValue<string>("Overlay:X");
            var yConfig = _config.GetValue<string>("Overlay:Y");
            				var x = "0";
            // Keep the raw overlay.Y value (don't default to 20 here) so we can decide
            // whether to honor an explicit config or compute a bottom-anchored value.
            var y = yConfig;

            // Build filter chain
            string esc(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
            var filters = new List<string>();
            filters.Add("format=yuv420p");

            // Keep the working drawbox (do not touch as requested)
            var bannerFraction = _config.GetValue<double?>("Overlay:BannerFraction") ?? 0.2;
            if (bannerFraction < 0) bannerFraction = 0; if (bannerFraction > 0.6) bannerFraction = 0.6;
            var bf = bannerFraction.ToString(CultureInfo.InvariantCulture);
            var drawbox = $"drawbox=x=0:y=ih*(1-{bf}):w=iw:h=ih*{bf}:color={boxColor}:t=fill";
            filters.Add(drawbox);

            // Estimate text banner height similar to FfmpegStreamer so we can place text inside the box when X/Y not provided
            int lineCount = 1;
            try
            {
                var initialText = System.IO.File.Exists(_overlayText.TextFilePath) ? System.IO.File.ReadAllText(_overlayText.TextFilePath) : string.Empty;
                if (!string.IsNullOrEmpty(initialText)) lineCount = initialText.Split('\n').Length;
            }
            catch { }
            var approxTextHeight = Math.Max(fontSize, 12) * Math.Max(1, lineCount);
            var padding = 32; // top+bottom padding approx
            var extra = 6; // small fudge for ascent/descent
            var boxH = padding + approxTextHeight + boxBorderW + extra;
            var boxHpx = padding + approxTextHeight + boxBorderW + extra; // pixel estimate used only for text placement when X/Y missing
            var textY = $"h-({boxH})+{padding / 2}";
            var textX = $"{x} + {padding / 2}";
            var draw = $"drawtext=fontfile='{fontFile}':textfile='{textFile}':reload=1:expansion=none:fontsize={fontSize}:fontcolor={fontColor}:x={textX}:y={textY}";

            filters.Add(draw);

            var vf = string.Join(",", filters);

            // Input args for HTTP MJPEG source
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
                                if (!string.IsNullOrWhiteSpace(s)) Console.WriteLine($"[OverlayMJPEG ffmpeg] {s}");
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
                _exitTcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                if (!_ctx.Response.HasStarted)
                {
                    _ctx.Response.StatusCode = 502;
                    await _ctx.Response.WriteAsync($"Overlay pipeline error: {ex.Message}", cancellationToken);
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
