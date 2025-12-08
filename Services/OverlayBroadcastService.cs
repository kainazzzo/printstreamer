using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintStreamer.Overlay;
using PrintStreamer.Streamers;

namespace PrintStreamer.Services;

/// <summary>
/// Runs a single ffmpeg process that serves the overlay MJPEG stream on a local HTTP listener.
/// This avoids spawning a new ffmpeg per client request.
/// </summary>
public sealed class OverlayBroadcastService : IDisposable
{
    private readonly IConfiguration _config;
    private readonly OverlayTextService _overlayText;
    private readonly ILogger<OverlayBroadcastService> _logger;
    private readonly OverlayProcessService _overlayProcessService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private string? _outputUrl;

    public OverlayBroadcastService(
        IConfiguration config,
        OverlayTextService overlayText,
        ILogger<OverlayBroadcastService> logger,
        OverlayProcessService overlayProcessService)
    {
        _config = config;
        _overlayText = overlayText;
        _logger = logger;
        _overlayProcessService = overlayProcessService;
    }

    public string OutputUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_outputUrl))
            {
                return _outputUrl!;
            }

            var configured = _config.GetValue<string>("Overlay:BroadcastUrl")?.Trim();
            _outputUrl = string.IsNullOrWhiteSpace(configured)
                ? "http://127.0.0.1:8091/overlay.mjpeg"
                : configured.TrimEnd('/');
            return _outputUrl!;
        }
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                return true;
            }

            try { _overlayText.Start(); } catch { }

            var source = _config.GetValue<string>("Overlay:StreamSource");
            if (string.IsNullOrWhiteSpace(source))
            {
                source = _config.GetValue<string>("Stream:Source") ?? "http://127.0.0.1:8080/stream/source";
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                _logger.LogError("[OverlayBroadcast] Stream source not configured");
                return false;
            }

            var fontFile = _config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf";
            var fontSize = _config.GetValue<int?>("Overlay:FontSize") ?? 16;
            var textFile = _overlayText.TextFilePath;
            var fontColor = _config.GetValue<string>("Overlay:FontColor") ?? "white";
            var boxColor = _config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4";
            var mjpegQ = _config.GetValue<int?>("Overlay:Quality") ?? 5;
            if (mjpegQ < 2) mjpegQ = 2; if (mjpegQ > 10) mjpegQ = 10;
            var filters = new List<string>();
            filters.Add("format=yuv420p");
            var boxHeightConfig = _config.GetValue<int?>("Overlay:BoxHeight") ?? 75;
            var layout = OverlayLayout.Calculate(_config, textFile, fontSize, boxHeightConfig);
            var drawbox = OverlayFilterUtil.BuildDrawbox(layout.DrawboxX, layout.DrawboxY, boxHeightConfig, boxColor);
            filters.Add(drawbox);
            var draw = $"drawtext=fontfile='{OverlayFilterUtil.Esc(fontFile)}':textfile='{OverlayFilterUtil.Esc(textFile)}':reload=1:expansion=none:fontsize={fontSize}:fontcolor={fontColor}:x={layout.TextX}:y={layout.TextY}";
            filters.Add(draw);
            var vf = string.Join(",", filters);
            _logger.LogInformation("[OverlayBroadcast] FFmpeg vf: {Vf}", vf);

            var inputArgs = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? $"-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2 -reconnect_on_network_error 1 -fflags +genpts+discardcorrupt -analyzeduration 5M -probesize 10M -max_delay 5000000 -f mjpeg -use_wallclock_as_timestamps 1 -i \"{source}\""
                : $"-i \"{source}\"";

            const string boundary = "frame";
            var args = string.Join(" ", new[]
            {
                "-hide_banner -nostats -loglevel error -nostdin -fflags nobuffer",
                inputArgs,
                "-vf",
                $"\"{vf}\"",
                "-an",
                $"-c:v mjpeg -huffman optimal -q:v {mjpegQ}",
                $"-f mpjpeg -boundary_tag {boundary} -listen 1 {OutputUrl}"
            });

            _proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            _proc.Exited += (_, __) =>
            {
                try { _proc?.Dispose(); } catch { }
                _proc = null;
            };

            _logger.LogInformation("[OverlayBroadcast] Starting ffmpeg overlay server → {Url}", OutputUrl);
            _proc.Start();
            _overlayProcessService.RegisterProcess(_proc);

            _ = Task.Run(async () =>
            {
                var buf = new char[1024];
                try
                {
                    while (_proc != null && !_proc.HasExited)
                    {
                        var n = await _proc.StandardError.ReadAsync(buf, 0, buf.Length);
                        if (n > 0)
                        {
                            var s = new string(buf, 0, n).Trim();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                if (s.Contains("unable to decode APP fields", StringComparison.OrdinalIgnoreCase) ||
                                    s.Contains("Last message repeated", StringComparison.OrdinalIgnoreCase))
                                {
                                    _logger.LogDebug("[OverlayBroadcast] Suppressed benign stderr: {Message}", s);
                                }
                                else
                                {
                                    _logger.LogWarning("[OverlayBroadcast] [ffmpeg stderr] {Message}", s);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[OverlayBroadcast] Error reading stderr");
                }
            }, cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OverlayBroadcast] Failed to start overlay broadcaster");
            try { _proc?.Kill(entireProcessTree: true); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        _gate.Wait();
        try
        {
            if (_proc != null)
            {
                try
                {
                    if (!_proc.HasExited)
                    {
                        _logger.LogInformation("[OverlayBroadcast] Stopping ffmpeg overlay server (PID {Pid})", _proc.Id);
                        _proc.Kill(entireProcessTree: true);
                        _proc.WaitForExit(2000);
                    }
                }
                catch { }
                finally
                {
                    try { _proc.Dispose(); } catch { }
                    _proc = null;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }
}
