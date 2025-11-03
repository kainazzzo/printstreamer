using PrintStreamer.Timelapse;

namespace PrintStreamer.Services
{
    /// <summary>
    /// Orchestrates streaming operations by coordinating between StreamService, 
    /// YouTubeControlService, and TimelapseManager.
    /// </summary>
    public class StreamOrchestrator : IDisposable
    {
        private readonly StreamService _streamService;
        private readonly IConfiguration _config;
        private readonly object _lock = new object();
        private YouTubeControlService? _currentYouTubeService;
        private string? _currentBroadcastId;
        private string? _currentRtmpUrl; // full RTMP URL (address + stream key) for restarts
        private bool _isWaitingForIngestion = false;
        private bool _disposed = false;

        public StreamOrchestrator(StreamService streamService, IConfiguration config)
        {
            _streamService = streamService;
            _config = config;
        }

        /// <summary>
        /// Check if a YouTube broadcast is currently active
        /// </summary>
        public bool IsBroadcastActive
        {
            get
            {
                lock (_lock)
                {
                    return !string.IsNullOrWhiteSpace(_currentBroadcastId);
                }
            }
        }

        /// <summary>
        /// Get the current broadcast ID if active
        /// </summary>
        public string? CurrentBroadcastId
        {
            get
            {
                lock (_lock)
                {
                    return _currentBroadcastId;
                }
            }
        }

        /// <summary>
        /// Check if the orchestrator is waiting for YouTube ingestion
        /// </summary>
        public bool IsWaitingForIngestion
        {
            get { return _isWaitingForIngestion; }
        }

        /// <summary>
        /// Check if a stream is currently running
        /// </summary>
        public bool IsStreaming => _streamService.IsStreaming;

        /// <summary>
        /// Start a YouTube live broadcast with streaming
        /// </summary>
        public async Task<(bool success, string? message, string? broadcastId)> StartBroadcastAsync(CancellationToken cancellationToken)
        {
            // Check if already broadcasting
            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(_currentBroadcastId))
                {
                    Console.WriteLine("[Orchestrator] Broadcast already active");
                    return (true, "Broadcast already active", _currentBroadcastId);
                }
            }

            // Check OAuth config
            var oauthClientId = _config.GetValue<string>("YouTube:OAuth:ClientId");
            var oauthClientSecret = _config.GetValue<string>("YouTube:OAuth:ClientSecret");
            if (string.IsNullOrWhiteSpace(oauthClientId) || string.IsNullOrWhiteSpace(oauthClientSecret))
            {
                return (false, "YouTube OAuth not configured", null);
            }

            // Create YouTube service and authenticate
            var ytService = new YouTubeControlService(_config);
            if (!await ytService.AuthenticateAsync(cancellationToken))
            {
                ytService.Dispose();
                return (false, "YouTube authentication failed", null);
            }

            // Create broadcast
            var result = await ytService.CreateLiveBroadcastAsync(cancellationToken);
            if (result.rtmpUrl == null || result.streamKey == null || result.broadcastId == null)
            {
                ytService.Dispose();
                return (false, "Failed to create YouTube broadcast", null);
            }

            var fullRtmpUrl = $"{result.rtmpUrl}/{result.streamKey}";
            var broadcastId = result.broadcastId;

            // Store YouTube service and broadcast ID
            lock (_lock)
            {
                _currentYouTubeService = ytService;
                _currentBroadcastId = broadcastId;
                _currentRtmpUrl = fullRtmpUrl;
            }

            Console.WriteLine($"[Orchestrator] Broadcast created: https://www.youtube.com/watch?v={broadcastId}");

            // Start stream with RTMP output
            try
            {
                await _streamService.StartStreamAsync(fullRtmpUrl, null, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Orchestrator] Failed to start stream: {ex.Message}");
                lock (_lock)
                {
                    _currentYouTubeService = null;
                    _currentBroadcastId = null;
                }
                ytService.Dispose();
                return (false, $"Failed to start stream: {ex.Message}", null);
            }

            // Start background task to transition broadcast to live when ready
            _ = Task.Run(async () =>
            {
                try
                {
                    _isWaitingForIngestion = true;
                    Console.WriteLine("[Orchestrator] Waiting for YouTube ingestion...");
                    var success = await ytService.TransitionBroadcastToLiveWhenReadyAsync(
                        broadcastId,
                        TimeSpan.FromSeconds(120),
                        5,
                        CancellationToken.None
                    );
                    Console.WriteLine($"[Orchestrator] Transition to live: {(success ? "success" : "failed")}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Orchestrator] Error transitioning to live: {ex.Message}");
                }
                finally
                {
                    _isWaitingForIngestion = false;
                }
            }, CancellationToken.None);

            return (true, null, broadcastId);
        }

        /// <summary>
        /// Ensure the streaming pipelines are healthy. If HLS is missing or stale, restart
        /// the appropriate stream: RTMP+HLS when broadcasting, or HLS-only otherwise.
        /// If broadcasting and ingestion does not become active after restart, end the broadcast
        /// but keep a local HLS stream running so the user can retry.
        /// </summary>
        public async Task<bool> EnsureStreamingHealthyAsync(bool requireHls, CancellationToken cancellationToken)
        {
            try
            {
                // Determine whether we should require HLS to be present.
                // Backwards-compatible: callers pass `requireHls`, but honor explicit configuration
                // Stream:Local:GenerateHls (bool) which when set to false indicates ffmpeg will not
                // produce HLS output and the orchestrator should not treat missing HLS as fatal.
                var configGeneratesHls = _config.GetValue<bool?>("Stream:Local:GenerateHls") ?? true;
                bool finalRequireHls = requireHls && configGeneratesHls && (_config.GetValue<bool?>("Stream:Local:Enabled") ?? false);

                // Determine HLS health (if required)
                bool hlsOk = true;
                if (finalRequireHls)
                {
                    var hlsFolder = _config.GetValue<string>("Stream:Local:HlsFolder") ?? "hls";
                    var manifest = Path.Combine(Directory.GetCurrentDirectory(), hlsFolder, "stream.m3u8");
                    hlsOk = File.Exists(manifest);
                    if (hlsOk)
                    {
                        try
                        {
                            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(manifest);
                            if (age > TimeSpan.FromSeconds(15)) hlsOk = false;
                        }
                        catch { }
                    }
                }

                // If stream service not running, or HLS not ok when required, restart
                if (!_streamService.IsStreaming || !hlsOk)
                {
                    Console.WriteLine("[Orchestrator] Stream health check failed (IsStreaming=" + _streamService.IsStreaming + ", HLS=" + hlsOk + ") — restarting...");
                    string? rtmp = null;
                    string? bid = null;
                    YouTubeControlService? yt;
                    lock (_lock)
                    {
                        yt = _currentYouTubeService;
                        bid = _currentBroadcastId;
                        rtmp = (!string.IsNullOrWhiteSpace(_currentBroadcastId)) ? _currentRtmpUrl : null;
                    }

                    // Restart the stream with current mode (RTMP+HLS when broadcasting; otherwise HLS-only)
                    await _streamService.StartStreamAsync(rtmp, null, cancellationToken);

                    // If broadcasting, nudge ingestion; if it fails, end broadcast but keep local HLS
                    if (!string.IsNullOrWhiteSpace(bid) && yt != null)
                    {
                        try
                        {
                            _isWaitingForIngestion = true;
                            var ok = await yt.TransitionBroadcastToLiveWhenReadyAsync(bid!, TimeSpan.FromSeconds(60), 6, cancellationToken);
                            if (!ok)
                            {
                                Console.WriteLine("[Orchestrator] Ingestion failed after restart; ending broadcast but keeping local HLS running");
                                await StopBroadcastKeepLocalAsync(cancellationToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Orchestrator] Error ensuring ingestion after restart: {ex.Message}");
                        }
                        finally
                        {
                            _isWaitingForIngestion = false;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Orchestrator] EnsureStreamingHealthyAsync error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// End the current YouTube broadcast but keep a local HLS stream running.
        /// </summary>
        public async Task StopBroadcastKeepLocalAsync(CancellationToken cancellationToken)
        {
            YouTubeControlService? ytService;
            string? broadcastId;

            lock (_lock)
            {
                ytService = _currentYouTubeService;
                broadcastId = _currentBroadcastId;
                _currentYouTubeService = null;
                _currentBroadcastId = null;
                _currentRtmpUrl = null;
            }

            if (ytService != null && !string.IsNullOrWhiteSpace(broadcastId))
            {
                try
                {
                    await ytService.EndBroadcastAsync(broadcastId!, cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Orchestrator] Error ending broadcast: {ex.Message}");
                }
                finally
                {
                    ytService.Dispose();
                }
            }

            // Ensure local HLS is running after ending broadcast
            try
            {
                await _streamService.StartStreamAsync(null, null, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Orchestrator] Failed to start local HLS after ending broadcast: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop the current broadcast and stream
        /// </summary>
        public async Task<(bool success, string? message)> StopBroadcastAsync(CancellationToken cancellationToken)
        {
            YouTubeControlService? ytService;
            string? broadcastId;

            lock (_lock)
            {
                ytService = _currentYouTubeService;
                broadcastId = _currentBroadcastId;

                _currentYouTubeService = null;
                _currentBroadcastId = null;
            }

            if (ytService == null || string.IsNullOrWhiteSpace(broadcastId))
            {
                // Just stop the stream if no broadcast
                await _streamService.StopStreamAsync();
                return (true, "Stream stopped (no broadcast was active)");
            }

            Console.WriteLine("[Orchestrator] Stopping broadcast and stream...");

            // Stop stream first
            await _streamService.StopStreamAsync();

            // End YouTube broadcast
            try
            {
                await ytService.EndBroadcastAsync(broadcastId, cancellationToken);
                Console.WriteLine($"[Orchestrator] Broadcast ended: {broadcastId}");

                // Add to playlist if configured
                try
                {
                    await Task.Delay(2000, cancellationToken); // Wait for YouTube processing
                    var playlistName = _config.GetValue<string>("YouTube:Playlist:Name");
                    if (!string.IsNullOrWhiteSpace(playlistName))
                    {
                        var playlistPrivacy = _config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted";
                        var pid = await ytService.EnsurePlaylistAsync(playlistName, playlistPrivacy, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(pid))
                        {
                            await ytService.AddVideoToPlaylistAsync(pid, broadcastId, cancellationToken);
                            Console.WriteLine($"[Orchestrator] Added broadcast to playlist '{playlistName}'");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Orchestrator] Failed to add to playlist: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Orchestrator] Error ending broadcast: {ex.Message}");
                return (false, $"Error ending broadcast: {ex.Message}");
            }
            finally
            {
                ytService.Dispose();
            }

            return (true, "Broadcast and stream stopped");
        }

        /// <summary>
        /// Start a local HLS-only stream (no YouTube broadcast)
        /// </summary>
        public async Task StartLocalStreamAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("[Orchestrator] Starting local HLS-only stream");
            await _streamService.StartStreamAsync(null, null, cancellationToken);
        }

        /// <summary>
        /// Stop any active stream (local or broadcast)
        /// </summary>
        public async Task StopStreamAsync()
        {
            await _streamService.StopStreamAsync();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                try { _currentYouTubeService?.Dispose(); } catch { }
                _currentYouTubeService = null;
                _currentBroadcastId = null;
            }
        }
    }
}
