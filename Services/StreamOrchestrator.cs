using PrintStreamer.Timelapse;
using Microsoft.Extensions.Logging;

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
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<StreamOrchestrator> _logger;
        private readonly YouTubePollingManager _pollingManager;
        private readonly object _lock = new object();
        private YouTubeControlService? _currentYouTubeService;
        private string? _currentBroadcastId;
        private string? _currentRtmpUrl; // full RTMP URL (address + stream key) for restarts
        private bool _isWaitingForIngestion = false;
        private bool _disposed = false;
        private bool _endStreamAfterSong = false;

        public StreamOrchestrator(StreamService streamService, IConfiguration config, ILoggerFactory loggerFactory, YouTubePollingManager pollingManager)
        {
            _streamService = streamService;
            _config = config;
            _loggerFactory = loggerFactory;
            _pollingManager = pollingManager;
            _logger = loggerFactory.CreateLogger<StreamOrchestrator>();
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
        /// Set the flag to end the stream after the current audio track finishes
        /// </summary>
        public void SetEndStreamAfterSong(bool enabled)
        {
            lock (_lock)
            {
                    _endStreamAfterSong = enabled;
                }
                _logger.LogInformation("[Orchestrator] End stream after song: {Enabled}", (enabled ? "enabled" : "disabled"));
        }

        /// <summary>
        /// Check if the stream is set to end after the current song
        /// </summary>
        public bool IsEndStreamAfterSongEnabled
        {
            get
            {
                lock (_lock)
                {
                    return _endStreamAfterSong;
                }
            }
        }

        /// <summary>
        /// Called when an audio track finishes. If end-after-song is enabled, stops the broadcast.
        /// </summary>
        public async Task OnAudioTrackFinishedAsync()
        {
            bool shouldEnd;
            lock (_lock)
            {
                shouldEnd = _endStreamAfterSong;
                if (shouldEnd)
                {
                    _endStreamAfterSong = false; // Reset flag after use
                }
            }

            if (shouldEnd)
            {
                _logger.LogInformation("[Orchestrator] Audio track finished, ending stream as requested");
                try
                {
                    await StopBroadcastAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Orchestrator] Error ending stream after song");
                }
            }
        }

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
                    _logger.LogInformation("[Orchestrator] Broadcast already active");
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
            var ytLogger = _loggerFactory.CreateLogger<YouTubeControlService>();
            var ytService = new YouTubeControlService(_config, ytLogger, _pollingManager);
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

            _logger.LogInformation("[Orchestrator] Broadcast created: https://www.youtube.com/watch?v={BroadcastId}", broadcastId);

            // Start stream with RTMP output
            try
            {
                await _streamService.StartStreamAsync(fullRtmpUrl, null, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Orchestrator] Failed to start stream");
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
                    _logger.LogInformation("[Orchestrator] Waiting for YouTube ingestion...");
                    var success = await ytService.TransitionBroadcastToLiveWhenReadyAsync(
                        broadcastId,
                        TimeSpan.FromSeconds(120),
                        5,
                        CancellationToken.None
                    );
                    _logger.LogInformation("[Orchestrator] Transition to live: {Result}", (success ? "success" : "failed"));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Orchestrator] Error transitioning to live");
                }
                finally
                {
                    _isWaitingForIngestion = false;
                }
            }, CancellationToken.None);

            return (true, null, broadcastId);
        }

        /// <summary>
        /// Ensure the streaming pipelines are healthy. If stream is not running, restart it.
        /// If broadcasting and ingestion does not become active after restart, end the broadcast
        /// but keep a local stream running so the user can retry.
        /// </summary>
        public async Task<bool> EnsureStreamingHealthyAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Check if stream service is running
                if (!_streamService.IsStreaming)
                {
                    _logger.LogWarning("[Orchestrator] Stream health check failed (IsStreaming=False) — restarting...");
                    string? rtmp = null;
                    string? bid = null;
                    YouTubeControlService? yt;
                    lock (_lock)
                    {
                        yt = _currentYouTubeService;
                        bid = _currentBroadcastId;
                        rtmp = (!string.IsNullOrWhiteSpace(_currentBroadcastId)) ? _currentRtmpUrl : null;
                    }

                    // Restart the stream with current mode (RTMP when broadcasting; otherwise local-only)
                    await _streamService.StartStreamAsync(rtmp, null, cancellationToken);

                    // If broadcasting, nudge ingestion; if it fails, end broadcast but keep local stream
                    if (!string.IsNullOrWhiteSpace(bid) && yt != null)
                    {
                        try
                        {
                            _isWaitingForIngestion = true;
                            var ok = await yt.TransitionBroadcastToLiveWhenReadyAsync(bid!, TimeSpan.FromSeconds(60), 6, cancellationToken);
                            if (!ok)
                            {
                                _logger.LogWarning("[Orchestrator] Ingestion failed after restart; ending broadcast but keeping local stream running");
                                await StopBroadcastKeepLocalAsync(cancellationToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[Orchestrator] Error ensuring ingestion after restart");
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
                _logger.LogError(ex, "[Orchestrator] EnsureStreamingHealthyAsync error");
                return false;
            }
        }

        /// <summary>
        /// End the current YouTube broadcast but keep a local stream running.
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
                    _logger.LogError(ex, "[Orchestrator] Error ending broadcast");
                }
                finally
                {
                    ytService.Dispose();
                }
            }

            // Ensure local stream is running after ending broadcast
            try
            {
                await _streamService.StartStreamAsync(null, null, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Orchestrator] Failed to start local stream after ending broadcast");
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

                _logger.LogInformation("[Orchestrator] Stopping broadcast and stream...");

            // Stop stream first
                await _streamService.StopStreamAsync();

            // End YouTube broadcast
            try
            {
                await ytService.EndBroadcastAsync(broadcastId, cancellationToken);
                _logger.LogInformation("[Orchestrator] Broadcast ended: {BroadcastId}", broadcastId);

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
                            _logger.LogInformation("[Orchestrator] Added broadcast to playlist '{PlaylistName}'", playlistName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Orchestrator] Failed to add to playlist");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Orchestrator] Error ending broadcast");
                return (false, $"Error ending broadcast: {ex.Message}");
            }
            finally
            {
                ytService.Dispose();
            }

            return (true, "Broadcast and stream stopped");
        }

        /// <summary>
        /// Start a local stream (no YouTube broadcast)
        /// </summary>
        public async Task StartLocalStreamAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Orchestrator] Starting local stream");
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
