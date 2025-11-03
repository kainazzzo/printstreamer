using PrintStreamer.Interfaces;
using PrintStreamer.Streamers;
using PrintStreamer.Overlay;

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
        private IStreamer? _currentStreamer;
        private CancellationTokenSource? _currentCts;
        private OverlayTextService? _overlayService;
        private bool _disposed = false;

        public StreamService(IConfiguration config, AudioService audioService)
        {
            _config = config;
            _audioService = audioService;
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
        /// <param name="overlayProvider">Optional metadata provider for overlay text</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task StartStreamAsync(string? rtmpUrl, ITimelapseMetadataProvider? overlayProvider, CancellationToken cancellationToken)
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
                _overlayService = null;
            }

            // Stop old stream outside the lock
            if (oldStreamer != null)
            {
                Console.WriteLine("[StreamService] Stopping existing stream...");
                try { oldCts?.Cancel(); } catch { }
                try { await Task.WhenAny(oldStreamer.ExitTask, Task.Delay(5000, cancellationToken)); } catch { }
                try { oldStreamer.Stop(); } catch { }
                try { oldOverlay?.Dispose(); } catch { }
            }

            // Prepare stream configuration
            var source = _config.GetValue<string>("Stream:Source");
            var serveEnabled = _config.GetValue<bool?>("Serve:Enabled") ?? true;
            if (serveEnabled)
            {
                // Use the overlay MJPEG endpoint as the ffmpeg source so ffmpeg pulls the composited
                // overlay over HTTP and can mix audio separately (YouTube ingestion will work as before).
                source = "http://127.0.0.1:8080/stream/overlay";
                Console.WriteLine("[StreamService] Using local overlay proxy stream as ffmpeg source (http://127.0.0.1:8080/stream/overlay)");
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
            var audioFeatureEnabled = _config.GetValue<bool?>("Audio:Enabled") ?? _config.GetValue<bool?>("audio:enabled") ?? true;
            if (serveEnabled && useApiAudio && audioFeatureEnabled)
            {
                audioUrl = _config.GetValue<string>("Stream:Audio:Url");
                if (string.IsNullOrWhiteSpace(audioUrl))
                {
                    audioUrl = "http://127.0.0.1:8080/api/audio/stream";
                }
            }

            // Setup overlay options only when NOT using the pre-composited overlay endpoint
            FfmpegOverlayOptions? overlayOptions = null;
            OverlayTextService? newOverlayService = null;
            if (!serveEnabled && (_config.GetValue<bool?>("Overlay:Enabled") ?? false))
            {
                try
                {
                    newOverlayService = new OverlayTextService(_config, overlayProvider, () => _audioService.Current);
                    newOverlayService.Start();
                    overlayOptions = new FfmpegOverlayOptions
                    {
                        TextFile = newOverlayService.TextFilePath,
                        FontFile = _config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
                        FontSize = _config.GetValue<int?>("Overlay:FontSize") ?? 22,
                        FontColor = _config.GetValue<string>("Overlay:FontColor") ?? "white",
                        Box = _config.GetValue<bool?>("Overlay:Box") ?? true,
                        BoxColor = _config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4",
                        BoxBorderW = _config.GetValue<int?>("Overlay:BoxBorderW") ?? 8,
                        X = _config.GetValue<string>("Overlay:X") ?? "(w-tw)-20",
                        Y = _config.GetValue<string>("Overlay:Y") ?? string.Empty,
                        BannerFraction = _config.GetValue<double?>("Overlay:BannerFraction") ?? 0.2
                    };
                    Console.WriteLine($"[StreamService] Overlay enabled (ffmpeg): {overlayOptions.TextFile}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StreamService] Failed to start overlay (ffmpeg): {ex.Message}");
                }
            }

            // Create new streamer
            var streamer = new FfmpegStreamer(source, rtmpUrl, targetFps, bitrateKbps, overlayOptions, audioUrl);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            lock (_lock)
            {
                _currentStreamer = streamer;
                _currentCts = cts;
                _overlayService = newOverlayService;
            }

            // Start streaming in background
            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[StreamService] Starting stream to {(rtmpUrl != null ? "RTMP" : "local")}");
                    await streamer.StartAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("[StreamService] Stream cancelled");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StreamService] Stream error: {ex.Message}");
                }
                finally
                {
                    Console.WriteLine("[StreamService] Stream ended");
                }
            }, cts.Token);

            Console.WriteLine("[StreamService] Stream started successfully");
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
                _overlayService = null;
            }

            if (streamer == null)
            {
                Console.WriteLine("[StreamService] No active stream to stop");
                return;
            }

            Console.WriteLine("[StreamService] Stopping stream...");
            try { cts?.Cancel(); } catch { }
            try { await Task.WhenAny(streamer.ExitTask, Task.Delay(5000)); } catch { }
            try { streamer.Stop(); } catch { }
            try { overlay?.Dispose(); } catch { }
            Console.WriteLine("[StreamService] Stream stopped");
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
                try { _overlayService?.Dispose(); } catch { }

                _currentStreamer = null;
                _currentCts = null;
                _overlayService = null;
            }
        }
    }
}
