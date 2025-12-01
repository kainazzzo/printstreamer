using PrintStreamer.Interfaces;
using PrintStreamer.Streamers;
using PrintStreamer.Overlay;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Services
{
    /// <summary>
    /// Singleton service that manages the ffmpeg streaming process.
    /// Ensures only ONE stream can be active at any time.
    /// </summary>
    public class StreamService : IDisposable
    {
        private readonly object _lock = new object();
    private readonly IConfiguration _config;
    private readonly AudioService _audioService;
    private readonly MoonrakerClient _moonrakerClient;
        private IStreamer? _currentStreamer;
        private CancellationTokenSource? _currentCts;
        private readonly OverlayTextService _overlayService;
        private bool _disposed = false;
        private readonly ILogger<StreamService> _logger;
        private readonly ILogger<FfmpegStreamer> _ffmpegLogger;

        public StreamService(IConfiguration config, AudioService audioService, ILogger<StreamService> logger, MoonrakerClient moonrakerClient, OverlayTextService overlayService, ILogger<FfmpegStreamer> ffmpegLogger)
        {
            _config = config;
            _audioService = audioService;
            _logger = logger;
            _ffmpegLogger = ffmpegLogger;
            _moonrakerClient = moonrakerClient;
            _overlayService = overlayService;
        }

        /// <summary>
        /// Check if a stream is currently active
        /// </summary>
        public bool IsStreaming
        {
            get
            {
                lock (_lock)
                {
                    return _currentStreamer != null && (_currentStreamer.ExitTask == null || !_currentStreamer.ExitTask.IsCompleted);
                }
            }
        }

        /// <summary>
        /// Start a new stream. If one is already running, it will be stopped first.
        /// </summary>
        /// <param name="rtmpUrl">Optional RTMP URL (e.g., rtmp://a.rtmp.youtube.com/live2/streamkey). If null, local preview only.</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task StartStreamAsync(string? rtmpUrl, CancellationToken cancellationToken)
        {
            IStreamer? oldStreamer = null;
            CancellationTokenSource? oldCts = null;
            OverlayTextService? oldOverlay = null;

            lock (_lock)
            {
                // Save references to old resources for cleanup
                oldStreamer = _currentStreamer;
                oldCts = _currentCts;
                oldOverlay = _overlayService;

                // Clear current state immediately
                _currentStreamer = null;
                _currentCts = null;
            }

            // Stop old stream outside the lock
            if (oldStreamer != null)
            {
                _logger.LogInformation("[StreamService] Stopping existing stream...");
                try { oldCts?.Cancel(); } catch { }
                try { await Task.WhenAny(oldStreamer.ExitTask, Task.Delay(5000, cancellationToken)); } catch { }
                try { oldStreamer.Stop(); } catch { }
                try { oldOverlay?.Dispose(); } catch { }
            }

            // Prepare stream configuration
            var source = _config.GetValue<string>("Stream:Source");
            var mixEnabled = _config.GetValue<bool?>("Stream:Mix:Enabled") ?? true;
            if (mixEnabled)
            {
                // Use the pre-mixed /stream/mix endpoint which combines video+audio from the pipeline
                // This allows FfmpegStreamer to simply read the mixed stream and broadcast to YouTube
                source = "http://127.0.0.1:8080/stream/mix";
                _logger.LogInformation("[StreamService] Using local mixed stream as ffmpeg source (http://127.0.0.1:8080/stream/mix)");
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                throw new InvalidOperationException("Stream:Source is not configured");
            }

            var targetFps = _config.GetValue<int?>("Stream:TargetFps") ?? 30;
            var bitrateKbps = _config.GetValue<int?>("Stream:BitrateKbps") ?? 2500;
            var localStreamEnabled = _config.GetValue<bool?>("Stream:Local:Enabled") ?? false;

            // Determine audio source for ffmpeg: prefer API endpoint when serving locally and enabled
            string? audioUrl = null;
            var useApiAudio = _config.GetValue<bool?>("Stream:Audio:UseApiStream") ?? true;
            var audioFeatureEnabled = _config.GetValue<bool?>("Audio:Enabled") ?? true;
            if (mixEnabled && useApiAudio && audioFeatureEnabled)
            {
                audioUrl = _config.GetValue<string>("Stream:Audio:Url");
                if (string.IsNullOrWhiteSpace(audioUrl))
                {
                    audioUrl = "http://127.0.0.1:8080/api/audio/stream";
                }
            }

            // Setup overlay options only when NOT using the pre-composited overlay endpoint
            FfmpegOverlayOptions? overlayOptions = null;
            if (!mixEnabled && (_config.GetValue<bool?>("Overlay:Enabled") ?? false))
            {
                try
                {
                    overlayOptions = new FfmpegOverlayOptions
                    {
                        TextFile = _overlayService.TextFilePath,
                        FontFile = _config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
                        FontSize = _config.GetValue<int?>("Overlay:FontSize") ?? 22,
                        FontColor = _config.GetValue<string>("Overlay:FontColor") ?? "white",
                        Box = _config.GetValue<bool?>("Overlay:Box") ?? true,
                        BoxColor = _config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4",
                        BoxBorderW = _config.GetValue<int?>("Overlay:BoxBorderW") ?? 2,
                        X = _config.GetValue<string>("Overlay:X") ?? "0",
                        Y = _config.GetValue<string>("Overlay:Y") ?? "40",
                        BannerFraction = _config.GetValue<double?>("Overlay:BannerFraction") ?? 0.2
                    };
                    _logger.LogInformation("[StreamService] Overlay enabled (ffmpeg): {TextFile}", overlayOptions.TextFile);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[StreamService] Failed to setup overlay (ffmpeg)");
                }
            }

            // Create new streamer
            var streamer = new FfmpegStreamer(source, rtmpUrl, targetFps, bitrateKbps, overlayOptions, audioUrl, _ffmpegLogger);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            lock (_lock)
            {
                _currentStreamer = streamer;
                _currentCts = cts;
            }

            // Start streaming in background
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("[StreamService] Starting stream to {Destination}", rtmpUrl != null ? "RTMP" : "local");
                    await streamer.StartAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("[StreamService] Stream cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[StreamService] Stream error");
                }
                finally
                {
                    _logger.LogInformation("[StreamService] Stream ended");
                }
            }, cts.Token);

            _logger.LogInformation("[StreamService] Stream started successfully");
        }

        /// <summary>
        /// Stop the current stream if one is running
        /// </summary>
        public async Task StopStreamAsync()
        {
            IStreamer? streamer;
            CancellationTokenSource? cts;
            OverlayTextService? overlay;

            lock (_lock)
            {
                streamer = _currentStreamer;
                cts = _currentCts;
                overlay = _overlayService;

                _currentStreamer = null;
                _currentCts = null;
            }

            if (streamer == null)
            {
                _logger.LogInformation("[StreamService] No active stream to stop");
                return;
            }

            _logger.LogInformation("[StreamService] Stopping stream...");
            try { cts?.Cancel(); } catch { }
            try { await Task.WhenAny(streamer.ExitTask, Task.Delay(5000)); } catch { }
            try { streamer.Stop(); } catch { }
            try { overlay?.Dispose(); } catch { }
            _logger.LogInformation("[StreamService] Stream stopped");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Synchronous cleanup
            lock (_lock)
            {
                try { _currentCts?.Cancel(); } catch { }
                try { _currentStreamer?.Stop(); } catch { }

                _currentStreamer = null;
                _currentCts = null;
            }
        }
    }
}
