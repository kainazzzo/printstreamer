using PrintStreamer.Streamers;
using PrintStreamer.Timelapse;
using PrintStreamer.Overlay;
using PrintStreamer.Utils;
using PrintStreamer.Interfaces;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Services
{
    internal static class MoonrakerPoller
    {
    // Shared runtime state to allow promoting the currently-running encoder to a live broadcast
        private static readonly object _streamLock = new object();
        private static IStreamer? _currentStreamer;
        private static CancellationTokenSource? _currentStreamerCts;
        private static YouTubeControlService? _currentYouTubeService;
        private static string? _currentBroadcastId;
    // Monitor state exposed to the UI
    private static volatile bool _isWaitingForIngestion = false;
    // Semaphore to prevent concurrent broadcast creation
    private static readonly SemaphoreSlim _broadcastCreationLock = new SemaphoreSlim(1, 1);

    // Camera simulation and blackout logic is handled only by WebCamManager.

        /// <summary>
        /// Cancel the current streamer (if any) and start a new one using the provided configuration.
        /// This is useful to force ffmpeg to re-read the configured source (for example when toggling
        /// camera simulation so the new streamer can use the local proxy endpoint).
        /// </summary>
        public static void RestartCurrentStreamerWithConfig(IConfiguration config, ILoggerFactory loggerFactory)
        {
            var logger = loggerFactory.CreateLogger(nameof(MoonrakerPoller));
            Task.Run(async () =>
            {
                IStreamer? old; CancellationTokenSource? oldCts;
                lock (_streamLock)
                {
                    old = _currentStreamer;
                    oldCts = _currentStreamerCts;
                    _currentStreamer = null;
                    _currentStreamerCts = null;
                }

                try
                {
                    try { oldCts?.Cancel(); } catch { }
                    try { await Task.WhenAny(old?.ExitTask ?? Task.CompletedTask, Task.Delay(2000)); } catch { }
                    try { old?.Stop(); } catch { }
                }
                catch { }

                // Start a new streamer in background. Use a linked CTS that can be cancelled by callers via config-driven mechanisms.
                try
                {
                    var cts = new CancellationTokenSource();
                    lock (_streamLock)
                    {
                        // We'll start the new streamer, but do not set _currentStreamer here because StartYouTubeStreamAsync will set it
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await StartYouTubeStreamAsync(config, loggerFactory, cts.Token, enableTimelapse: false, timelapseProvider: null, allowYouTube: false);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "[RestartStreamer] Error starting new streamer");
                        }
                    }, cts.Token);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[RestartStreamer] Failed to restart streamer");
                }
            });
        }

        public static bool IsBroadcastActive => !string.IsNullOrWhiteSpace(_currentBroadcastId);
        public static string? CurrentBroadcastId => _currentBroadcastId;
    public static bool IsStreamerRunning => _currentStreamer != null;
    public static bool IsWaitingForIngestion => _isWaitingForIngestion;

        /// <summary>
        /// Promote the currently-running encoder to a YouTube live broadcast by creating
        /// the broadcast resources and restarting the ffmpeg process to include RTMP output.
        /// Returns true on success.
        /// </summary>
        public static async Task<(bool ok, string? message, string? broadcastId)> StartBroadcastAsync(IConfiguration config, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
        {
            var pollerLogger = loggerFactory.CreateLogger(nameof(MoonrakerPoller));
            // Use semaphore to prevent concurrent broadcast creation
                if (!await _broadcastCreationLock.WaitAsync(0, cancellationToken))
            {
                pollerLogger.LogInformation("[StartBroadcast] Another broadcast creation is already in progress, skipping duplicate");
                // Wait briefly to see if the other operation completes and return its result
                await Task.Delay(500, cancellationToken);
                if (IsBroadcastActive)
                {
                    return (true, "Broadcast created by concurrent operation", _currentBroadcastId);
                }
                return (false, "Another broadcast creation is in progress", null);
            }

            try
            {
                // Check if a broadcast is already active - don't create a duplicate
                if (IsBroadcastActive)
                {
                    pollerLogger.LogInformation("[StartBroadcast] A broadcast is already active, skipping duplicate creation");
                    return (true, "Broadcast already active", _currentBroadcastId);
                }

                // Only supports OAuth flow for automatic broadcast creation
                var oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
                var oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");
                bool useOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);
                if (!useOAuth)
                {
                    return (false, "OAuth client credentials not configured", null);
                }

            // In some flows the local streamer may be restarting (e.g., camera toggle or prior stop).
            // Give it a short grace period to appear; if still null, we'll start a fresh encoder below.
            if (_currentStreamer == null)
            {
                var waitUntil = DateTime.UtcNow.AddSeconds(3);
                while (_currentStreamer == null && DateTime.UtcNow < waitUntil)
                {
                    try { await Task.Delay(100, cancellationToken); } catch { break; }
                }
            }

            // Authenticate and create broadcast
            var logger = loggerFactory.CreateLogger<YouTubeControlService>();
            var yt = new YouTubeControlService(config, logger);
            if (!await yt.AuthenticateAsync(cancellationToken))
            {
                yt.Dispose();
                return (false, "YouTube authentication failed", null);
            }

            var res = await yt.CreateLiveBroadcastAsync(cancellationToken);
            if (res.rtmpUrl == null || res.streamKey == null)
            {
                yt.Dispose();
                return (false, "Failed to create YouTube broadcast", null);
            }

            var newRtmp = res.rtmpUrl;
            var newKey = res.streamKey;
            var newBroadcastId = res.broadcastId;

            // Restart streamer if one is running: cancel current, then start a new one with RTMP
            IStreamer? old; CancellationTokenSource? oldCts;
            lock (_streamLock)
            {
                old = _currentStreamer;
                oldCts = _currentStreamerCts;
                _currentStreamer = null;
                _currentStreamerCts = null;
            }

            try
            {
                // Stop the old streamer if it exists
                if (old != null)
                {
                    try { oldCts?.Cancel(); } catch { }
                    try { await Task.WhenAny(old.ExitTask, Task.Delay(5000, cancellationToken)); } catch { }
                    try { old.Stop(); } catch { }
                }
            }
            catch { }

            // Start new ffmpeg streamer with RTMP
            try
            {
                var localStreamEnabled = config.GetValue<bool?>("Stream:Local:Enabled") ?? false;
                var targetFps = config.GetValue<int?>("Stream:TargetFps") ?? 30;
                var bitrateKbps = config.GetValue<int?>("Stream:BitrateKbps") ?? 2500;

                // Determine audio source for ffmpeg when serving locally
                string? audioUrl = null;
                var useApiAudio = config.GetValue<bool?>("Stream:Audio:UseApiStream") ?? true;
                var audioFeatureEnabled = config.GetValue<bool?>("Audio:Enabled") ?? config.GetValue<bool?>("audio:enabled") ?? true;
                if ((config.GetValue<bool?>("Serve:Enabled") ?? true) && useApiAudio && audioFeatureEnabled)
                {
                    audioUrl = config.GetValue<string>("Stream:Audio:Url");
                    if (string.IsNullOrWhiteSpace(audioUrl)) audioUrl = "http://127.0.0.1:8080/api/audio/stream";
                }

                

                // Overlay options: only when not using the pre-composited overlay endpoint
                FfmpegOverlayOptions? overlayOptions = null;
                if (!(config.GetValue<bool?>("Serve:Enabled") ?? true) && (config.GetValue<bool?>("Overlay:Enabled") ?? false))
                {
                    var overlayService = new OverlayTextService(config, null, null, loggerFactory.CreateLogger<OverlayTextService>());
                    overlayService.Start();
                    overlayOptions = new FfmpegOverlayOptions
                    {
                        TextFile = overlayService.TextFilePath,
                        FontFile = config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
                        FontSize = config.GetValue<int?>("Overlay:FontSize") ?? 22,
                        FontColor = config.GetValue<string>("Overlay:FontColor") ?? "white",
                        Box = config.GetValue<bool?>("Overlay:Box") ?? true,
                        BoxColor = config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4",
                        BoxBorderW = config.GetValue<int?>("Overlay:BoxBorderW") ?? 8,
                        X = config.GetValue<string>("Overlay:X") ?? "(w-tw)-20",
                        Y = config.GetValue<string>("Overlay:Y") ?? "20"
                    };
                }

                var source = config.GetValue<string>("Stream:Source");
                var serveEnabled = config.GetValue<bool?>("Serve:Enabled") ?? true;
                // Always use local overlay proxy when web server is enabled - this ensures overlays and camera simulation work
                // and provides consistency for ffmpeg pulling the composited stream over HTTP.
                if (serveEnabled)
                {
                    source = "http://127.0.0.1:8080/stream/overlay";
                    pollerLogger.LogInformation("[Stream] Using local overlay proxy stream as ffmpeg source (http://127.0.0.1:8080/stream/overlay)");
                }
                if (string.IsNullOrWhiteSpace(source))
                {
                    yt.Dispose();
                    return (false, "Stream:Source is not configured", null);
                }
                var streamer = new FfmpegStreamer(source, newRtmp + "/" + newKey, targetFps, bitrateKbps, overlayOptions, audioUrl, loggerFactory.CreateLogger<FfmpegStreamer>());
                var cts = new CancellationTokenSource();

                lock (_streamLock)
                {
                    _currentStreamer = streamer;
                    _currentStreamerCts = cts;
                    _currentYouTubeService = yt;
                    _currentBroadcastId = newBroadcastId;
                }

                _ = Task.Run(async () =>
                {
                    try { await streamer.StartAsync(cts.Token); } catch { }
                }, cts.Token);

                // After starting the ffmpeg streamer, attempt to transition the YouTube broadcast to live
                // This runs in the background and will wait for ingestion to become active before transitioning.
                if (!string.IsNullOrWhiteSpace(newBroadcastId))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            pollerLogger.LogInformation("[YouTube] Waiting for ingestion and attempting to transition broadcast to live...");
                            var ok = await yt.TransitionBroadcastToLiveWhenReadyAsync(newBroadcastId!, TimeSpan.FromSeconds(120), 5, CancellationToken.None);
                            pollerLogger.LogInformation("[YouTube] Transition to live {Result} for broadcast {BroadcastId}", ok ? "succeeded" : "failed", newBroadcastId);
                        }
                        catch (Exception ex)
                        {
                            pollerLogger.LogError(ex, "[YouTube] Error while transitioning broadcast to live");
                        }
                    }, CancellationToken.None);
                }
                else
                {
                    pollerLogger.LogInformation("[YouTube] No broadcastId available; skipping automatic transition to live.");
                }

                return (true, null, newBroadcastId);
            }
            catch (Exception ex)
            {
                yt.Dispose();
                return (false, ex.Message, null);
            }
        }
        catch (Exception outerEx)
        {
            pollerLogger.LogError(outerEx, "[StartBroadcast] Unexpected error");
            return (false, $"Unexpected error: {outerEx.Message}", null);
        }
        finally
        {
            _broadcastCreationLock.Release();
        }
    }

        /// <summary>
        /// Stop the current live broadcast and end the YouTube stream.
        /// Returns true on success.
        /// </summary>
        public static async Task<(bool ok, string? message)> StopBroadcastAsync(IConfiguration config, CancellationToken cancellationToken, ILoggerFactory loggerFactory)
        {
            var logger = loggerFactory.CreateLogger(nameof(MoonrakerPoller));
            IStreamer? streamer;
            CancellationTokenSource? cts;
            YouTubeControlService? yt;
            string? broadcastId;

            lock (_streamLock)
            {
                streamer = _currentStreamer;
                cts = _currentStreamerCts;
                yt = _currentYouTubeService;
                broadcastId = _currentBroadcastId;
                
                _currentStreamer = null;
                _currentStreamerCts = null;
                _currentYouTubeService = null;
                _currentBroadcastId = null;
            }

            if (streamer == null)
            {
                return (false, "No active broadcast to stop");
            }

            try
            {
                // Stop the streamer
                try { cts?.Cancel(); } catch { }
                try { await Task.WhenAny(streamer.ExitTask, Task.Delay(5000, cancellationToken)); } catch { }
                try { streamer.Stop(); } catch { }

                // End the YouTube broadcast
                if (yt != null && !string.IsNullOrWhiteSpace(broadcastId))
                {
                    try
                    {
                        await yt.EndBroadcastAsync(broadcastId, cancellationToken);
                        logger.LogInformation("[YouTube] Ended broadcast {BroadcastId}", broadcastId);
                        
                        // Add the completed broadcast to the playlist
                        try
                        {
                            // Wait a moment for YouTube to process the broadcast completion
                            await Task.Delay(2000, cancellationToken);
                            
                            var playlistName = config.GetValue<string>("YouTube:Playlist:Name");
                            if (!string.IsNullOrWhiteSpace(playlistName))
                            {
                                logger.LogInformation("[YouTube] Adding completed broadcast to playlist '{PlaylistName}'...", playlistName);
                                var playlistPrivacy = config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted";
                                var pid = await yt.EnsurePlaylistAsync(playlistName, playlistPrivacy, cancellationToken);
                                if (!string.IsNullOrWhiteSpace(pid))
                                {
                                    var added = await yt.AddVideoToPlaylistAsync(pid, broadcastId, cancellationToken);
                                    if (added)
                                    {
                                        logger.LogInformation("[YouTube] Successfully added broadcast to playlist '{PlaylistName}'", playlistName);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "[YouTube] Failed to add broadcast to playlist");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[YouTube] Error ending broadcast");
                    }
                    finally
                    {
                        yt.Dispose();
                    }
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[StopBroadcast] Error");
                return (false, ex.Message);
            }
        }

        // Entry point moved from Program.cs
    public static async Task PollAndStreamJobsAsync(IConfiguration config, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
        {
            var watcherLogger = loggerFactory.CreateLogger(nameof(MoonrakerPoller));
            var moonrakerBase = config.GetValue<string>("Moonraker:BaseUrl") ?? "http://localhost:7125/";
            var apiKey = config.GetValue<string>("Moonraker:ApiKey");
            var authHeader = config.GetValue<string>("Moonraker:AuthHeader");
            var basePollInterval = TimeSpan.FromSeconds(10); // configurable if desired
            var fastPollInterval = TimeSpan.FromSeconds(2); // faster polling near completion
            string? lastJobFilename = null;
            string? lastCompletedJobFilename = null; // used for final upload/title if app shuts down post-completion
            CancellationTokenSource? streamCts = null;
            Task? streamTask = null;
            TimelapseService? timelapse = null;
            CancellationTokenSource? timelapseCts = null;
            Task? timelapseTask = null;
            YouTubeControlService? ytService = null;
            TimelapseManager? timelapseManager = null;
            string? activeTimelapseSessionName = null; // track current timelapse session in manager
            // New state to support last-layer early finalize
            bool lastLayerTriggered = false;
            Task? timelapseFinalizeTask = null;

            try
            {
                // Initialize TimelapseManager for G-code caching and frame capture
                timelapseManager = new TimelapseManager(config, loggerFactory);
                
                // Initialize YouTube service if credentials are provided (for timelapse upload)
                var oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
                var oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");
                bool useOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);
                var liveBroadcastEnabled = config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true;
                if (useOAuth && liveBroadcastEnabled)
                {
                    var ytLogger = loggerFactory.CreateLogger<YouTubeControlService>();
                    ytService = new YouTubeControlService(config, ytLogger);
                    var authOk = await ytService.AuthenticateAsync(cancellationToken);
                    if (!authOk)
                    {
                        watcherLogger.LogWarning("[Watcher] YouTube authentication failed. Timelapse upload will be disabled.");
                        ytService.Dispose();
                        ytService = null;
                    }
                    else
                    {
                        watcherLogger.LogInformation("[Watcher] YouTube authenticated successfully for timelapse uploads.");
                    }
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    TimeSpan pollInterval = basePollInterval; // Default poll interval
                    try
                    {
                    // Query Moonraker job queue
                    var baseUri = new Uri(moonrakerBase);
                    var info = await MoonrakerClient.GetPrintInfoAsync(baseUri, apiKey, authHeader, cancellationToken);
                    var currentJob = info?.Filename;
                    var jobQueueId = info?.JobQueueId;
                    var state = info?.State;
                    var isPrinting = string.Equals(state, "printing", StringComparison.OrdinalIgnoreCase);
                    var remaining = info?.Remaining;
                    var progressPct = info?.ProgressPercent;
                    var currentLayer = info?.CurrentLayer;
                    var totalLayers = info?.TotalLayers;

                    watcherLogger.LogDebug("[Watcher] Poll result - Filename: '{Filename}', State: '{State}', Progress: {Progress}%, Remaining: {Remaining}, Layer: {Layer}/{Total}", currentJob, state, progressPct?.ToString("F1") ?? "n/a", remaining?.ToString() ?? "n/a", currentLayer?.ToString() ?? "n/a", totalLayers?.ToString() ?? "n/a");

                    // Inform TimelapseManager about current progress so it can stop capturing when last-layer threshold is reached
                    try
                    {
                        if (timelapseManager != null && activeTimelapseSessionName != null)
                        {
                            // NotifyPrintProgress is void - it just stops capturing frames internally when threshold is reached
                            // The actual finalization and upload is handled by the "else if" block below
                            timelapseManager.NotifyPrintProgress(activeTimelapseSessionName, currentLayer, totalLayers);
                        }
                    }
                    catch (Exception ex)
                    {
                        watcherLogger.LogError(ex, "[Watcher] Failed to notify timelapse manager of print progress");
                    }

                    // Track if a stream is already active
                    var streamingActive = streamCts != null && streamTask != null && !streamTask.IsCompleted;

                    // Start stream when actively printing (even if filename is missing initially)
                    if (isPrinting && !streamingActive && (string.IsNullOrWhiteSpace(currentJob) || currentJob != lastJobFilename))
                    {
                        // New job detected, start stream and timelapse
                        watcherLogger.LogInformation("[Watcher] New print job detected: {Job}", currentJob ?? "(unknown)");
                        lastJobFilename = currentJob ?? $"__printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                        if (streamCts != null)
                        {
                            try { streamCts.Cancel(); } catch { }
                            if (streamTask != null) await streamTask;
                        }
                        streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        streamTask = Task.Run(async () =>
                        {
                            try
                            {
                                // In polling mode, disable internal timelapse in streaming path to avoid duplicate uploads
                                // Also check if a broadcast is already active (from manual start); if so, don't create another
                                var alreadyBroadcasting = IsBroadcastActive;
                                await StartYouTubeStreamAsync(config, loggerFactory, streamCts.Token, enableTimelapse: false, timelapseProvider: null, allowYouTube: !alreadyBroadcasting);
                            }
                                catch (Exception ex)
                            {
                                watcherLogger.LogError(ex, "[Watcher] Stream error");
                            }
                        }, streamCts.Token);
                        // Start timelapse using TimelapseManager (will download G-code and cache metadata)
                        {
                            var jobNameSafe = !string.IsNullOrWhiteSpace(currentJob) ? SanitizeFilename(currentJob) : $"printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                            watcherLogger.LogInformation("[Watcher] Starting timelapse session: {Session}", jobNameSafe);
                            watcherLogger.LogInformation("[Watcher]   - currentJob: '{CurrentJob}'", currentJob);
                            watcherLogger.LogDebug("[Watcher]   - jobNameSafe: '{JobNameSafe}'", jobNameSafe);
                            
                            // Start timelapse via manager (downloads G-code, caches metadata, captures initial frame)
                            activeTimelapseSessionName = await timelapseManager!.StartTimelapseAsync(jobNameSafe, currentJob);
                            if (activeTimelapseSessionName != null)
                            {
                                watcherLogger.LogInformation("[Watcher] Timelapse session started: {Session}", activeTimelapseSessionName);
                            }
                            else
                            {
                                watcherLogger.LogWarning("[Watcher] Warning: Failed to start timelapse session");
                            }
                            
                            lastLayerTriggered = false; // reset for new job
                            timelapseFinalizeTask = null;
                            
                            // Note: TimelapseManager handles periodic frame capture internally via its timer
                            // No need for manual timelapseTask in poll mode
                        }
                    }
                    // Detect last-layer and finalize timelapse early (while keeping live stream running)
                    else if (isPrinting && activeTimelapseSessionName != null && !lastLayerTriggered)
                    {
                        // More aggressive defaults to catch the last layer of actual printing (not cooldown/retraction)
                        var thresholdSecs = config.GetValue<int?>("Timelapse:LastLayerRemainingSeconds") ?? 30;
                        var thresholdPct = config.GetValue<double?>("Timelapse:LastLayerProgressPercent") ?? 98.5;
                        var layerThreshold = config.GetValue<int?>("Timelapse:LastLayerOffset") ?? 1; // trigger one layer earlier (avoid one extra frame)
                        
                        bool lastLayerByTime = remaining.HasValue && remaining.Value <= TimeSpan.FromSeconds(thresholdSecs);
                        bool lastLayerByProgress = progressPct.HasValue && progressPct.Value >= thresholdPct;
                        bool lastLayerByLayer = currentLayer.HasValue && totalLayers.HasValue && 
                                                totalLayers.Value > 0 && 
                                                currentLayer.Value >= (totalLayers.Value - layerThreshold);
                        
                        if (lastLayerByTime || lastLayerByProgress || lastLayerByLayer)
                        {
                            watcherLogger.LogInformation("[Timelapse] *** Last-layer detected ***");
                            watcherLogger.LogDebug("[Timelapse] Remaining time: {Remaining} (threshold: {ThresholdSecs}s, triggered: {ByTime})", remaining?.ToString() ?? "n/a", thresholdSecs, lastLayerByTime);
                            watcherLogger.LogDebug("[Timelapse] Progress: {Progress}% (threshold: {ThresholdPct}%, triggered: {ByProgress})", progressPct?.ToString("F1") ?? "n/a", thresholdPct, lastLayerByProgress);
                            watcherLogger.LogDebug("[Timelapse] Layer: {Layer}/{Total} (threshold: -{LayerThreshold}, triggered: {ByLayer})", currentLayer?.ToString() ?? "n/a", totalLayers?.ToString() ?? "n/a", layerThreshold, lastLayerByLayer);
                            watcherLogger.LogInformation("[Timelapse] Capturing final frame and finalizing timelapse now...");
                            lastLayerTriggered = true;

                            // Stop timelapse via manager and kick off finalize/upload in the background
                            var sessionToFinalize = activeTimelapseSessionName;
                            var uploadEnabled = config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false;
                            timelapseFinalizeTask = Task.Run(async () =>
                            {
                                try
                                {
                                    watcherLogger.LogInformation("[Timelapse] Stopping timelapse session (early finalize): {Session}", sessionToFinalize);
                                    var createdVideoPath = await timelapseManager!.StopTimelapseAsync(sessionToFinalize!);

                                    if (!string.IsNullOrWhiteSpace(createdVideoPath) && File.Exists(createdVideoPath) && uploadEnabled && ytService != null)
                                    {
                                        try
                                        {
                                            watcherLogger.LogInformation("[Timelapse] Uploading timelapse video (early finalize) to YouTube...");
                                            var titleName = lastJobFilename ?? sessionToFinalize;
                                            var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, titleName!, CancellationToken.None);
                                            if (!string.IsNullOrWhiteSpace(videoId))
                                            {
                                                watcherLogger.LogInformation("[Timelapse] Early-upload complete: https://www.youtube.com/watch?v={VideoId}", videoId);
                                            }
                                        }
                                        catch (Exception upx)
                                        {
                                            watcherLogger.LogError(upx, "[Timelapse] Early-upload failed");
                                        }
                                    }
                                }
                                catch (Exception fex)
                                {
                                    watcherLogger.LogError(fex, "[Timelapse] Early finalize failed");
                                }
                            }, CancellationToken.None);

                            // Clear the active session reference so end-of-print path won't double-run
                            activeTimelapseSessionName = null;
                        }
                    }
                    else if (!isPrinting && (streamCts != null || streamTask != null))
                    {
                        // Job finished, end stream and finalize timelapse
                        watcherLogger.LogInformation("[Watcher] Print job finished: {Job}", lastJobFilename);
                        // Preserve the filename we detected at job start for upload metadata
                        var finishedJobFilename = lastJobFilename;
                        lastCompletedJobFilename = finishedJobFilename;
                        lastJobFilename = null;
                        
                        // Properly stop the broadcast (ends YouTube stream and cleans up) only in auto-broadcast mode.
                        // In manual mode (auto-broadcast disabled), keep the YouTube broadcast running until the user stops it.
                        var autoBroadcastEnabled = config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true;
                        if (autoBroadcastEnabled)
                        {
                            try
                            {
                                var (ok, msg) = await StopBroadcastAsync(config, CancellationToken.None, loggerFactory);
                                if (ok)
                                {
                                    watcherLogger.LogInformation("[Watcher] Broadcast stopped successfully");
                                }
                                else
                                {
                                    watcherLogger.LogWarning("[Watcher] Error stopping broadcast: {Message}", msg);
                                }
                            }
                            catch (Exception ex)
                            {
                                watcherLogger.LogError(ex, "[Watcher] Exception stopping broadcast");
                            }
                        }
                        else
                        {
                            watcherLogger.LogInformation("[Watcher] Auto-broadcast is disabled; leaving live broadcast running (manual mode).");
                        }
                        
                        // Clean up local stream references
                        if (streamCts != null)
                        {
                            try { streamCts.Cancel(); } catch { }
                            if (streamTask != null) await streamTask;
                            streamCts = null;
                            streamTask = null;
                        }
                        if (timelapseCts != null)
                        {
                            try { timelapseCts.Cancel(); } catch { }
                            if (timelapseTask != null)
                            {
                                try { await timelapseTask; } catch (OperationCanceledException) { /* Expected */ }
                            }
                            timelapseCts = null;
                            timelapseTask = null;
                        }
                        if (timelapseFinalizeTask != null)
                        {
                            // Early finalize already started; wait for it to complete
                            try { await timelapseFinalizeTask; } catch { }
                            timelapseFinalizeTask = null;
                        }
                        else if (activeTimelapseSessionName != null)
                        {
                            watcherLogger.LogInformation("[Timelapse] Stopping timelapse session (end of print): {Session}", activeTimelapseSessionName);
                            try
                            {
                                var createdVideoPath = await timelapseManager!.StopTimelapseAsync(activeTimelapseSessionName);
                                
                                // Upload the timelapse video to YouTube if enabled and video was created successfully
                                if (!string.IsNullOrWhiteSpace(createdVideoPath) && File.Exists(createdVideoPath))
                                {
                                    var uploadTimelapse = config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false;
                                    if (uploadTimelapse && ytService != null)
                                    {
                                        watcherLogger.LogInformation("[Timelapse] Uploading timelapse video to YouTube...");
                                        try
                                        {
                                            // Use the timelapse folder name (sanitized) for nicer titles
                                            var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, activeTimelapseSessionName, CancellationToken.None);
                                            if (!string.IsNullOrWhiteSpace(videoId))
                                            {
                                                watcherLogger.LogInformation("[Timelapse] Video uploaded successfully! https://www.youtube.com/watch?v={VideoId}", videoId);
                                                try
                                                {
                                                    var playlistName = config.GetValue<string>("YouTube:Playlist:Name");
                                                    if (!string.IsNullOrWhiteSpace(playlistName))
                                                    {
                                                        var playlistPrivacy = config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted";
                                                        var pid = await ytService.EnsurePlaylistAsync(playlistName, playlistPrivacy, CancellationToken.None);
                                                        if (!string.IsNullOrWhiteSpace(pid))
                                                        {
                                                            await ytService.AddVideoToPlaylistAsync(pid, videoId, CancellationToken.None);
                                                        }
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    watcherLogger.LogError(ex, "[YouTube] Failed to add timelapse to playlist");
                                                }
                                                    try
                                                    {
                                                        // Set thumbnail from last frame if available
                                                        var frameFiles = Directory.GetFiles(Path.GetDirectoryName(createdVideoPath) ?? string.Empty, "frame_*.jpg").OrderBy(f => f).ToArray();
                                                        if (frameFiles.Length > 0)
                                                        {
                                                            var lastFrame = frameFiles[^1];
                                                            var bytes = await File.ReadAllBytesAsync(lastFrame, CancellationToken.None);
                                                            var okThumb = await ytService.SetVideoThumbnailAsync(videoId, bytes, CancellationToken.None);
                                                            if (okThumb)
                                                            {
                                                                watcherLogger.LogInformation("[Timelapse] Set video thumbnail from last frame.");
                                                            }
                                                        }
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        watcherLogger.LogError(ex, "[Timelapse] Failed to set video thumbnail");
                                                    }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            watcherLogger.LogError(ex, "[Timelapse] Failed to upload video to YouTube");
                                        }
                                    }
                                    else if (!uploadTimelapse)
                                    {
                                        watcherLogger.LogInformation("[Timelapse] Video upload to YouTube is disabled (YouTube:TimelapseUpload:Enabled=false)");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                watcherLogger.LogError(ex, "[Timelapse] Failed to create video");
                            }
                            activeTimelapseSessionName = null;
                        }
                    }

                    // Adaptive polling: poll faster when we're near completion (must be inside try block)
                    pollInterval = basePollInterval;
                    if (isPrinting && timelapse != null && !lastLayerTriggered)
                    {
                        // Use fast polling if:
                        // - Less than 2 minutes remaining
                        // - More than 95% complete
                        // - Within 5 layers of completion
                        bool nearCompletion = (remaining.HasValue && remaining.Value <= TimeSpan.FromMinutes(2)) ||
                                              (progressPct.HasValue && progressPct.Value >= 95.0) ||
                                              (currentLayer.HasValue && totalLayers.HasValue && totalLayers.Value > 0 && 
                                               currentLayer.Value >= (totalLayers.Value - 5));
                        if (nearCompletion)
                        {
                            pollInterval = fastPollInterval;
                            watcherLogger.LogDebug("[Watcher] Using fast polling ({Seconds}s) - near completion", pollInterval.TotalSeconds);
                        }
                    }
                }
                catch (Exception ex)
                {
                    watcherLogger.LogError(ex, "[Watcher] Error");
                }

                await Task.Delay(pollInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                watcherLogger.LogInformation("[Watcher] Polling cancelled by user.");
            }
            catch (Exception ex)
            {
                watcherLogger.LogError(ex, "[Watcher] Unexpected error");
            }
            finally
            {
                // Cleanup on exit
                watcherLogger.LogInformation("[Watcher] Shutting down...");
                if (streamCts != null)
                {
                    try { streamCts.Cancel(); } catch { }
                    if (streamTask != null)
                    {
                        try { await streamTask; } catch { }
                    }
                }
                if (timelapseCts != null)
                {
                    try { timelapseCts.Cancel(); } catch { }
                    if (timelapseTask != null)
                    {
                        try { await timelapseTask; } catch (OperationCanceledException) { /* Expected */ }
                    }
                }
                if (timelapse != null)
                {
                    watcherLogger.LogInformation("[Timelapse] Creating video from {OutputDir}...", timelapse.OutputDir);
                    var folderName = Path.GetFileName(timelapse.OutputDir);
                    var videoPath = Path.Combine(timelapse.OutputDir, $"{folderName}.mp4");
                    try
                    {
                        var createdVideoPath = await timelapse.CreateVideoAsync(videoPath, 30, CancellationToken.None);
                        
                        // Upload the timelapse video to YouTube if enabled and video was created successfully
                        if (!string.IsNullOrWhiteSpace(createdVideoPath) && File.Exists(createdVideoPath))
                        {
                            var uploadTimelapse = config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false;
                                if (uploadTimelapse && ytService != null)
                                {
                                    watcherLogger.LogInformation("[Timelapse] Uploading timelapse video to YouTube...");
                                    try
                                    {
                                    // Prefer the recently finished job's filename; fallback to timelapse folder name
                                    var filenameForUpload = lastCompletedJobFilename ?? lastJobFilename ?? folderName;
                                    var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, filenameForUpload, CancellationToken.None);
                                        if (!string.IsNullOrWhiteSpace(videoId))
                                        {
                                            watcherLogger.LogInformation("[Timelapse] Video uploaded successfully! https://www.youtube.com/watch?v={VideoId}", videoId);
                                            // Add to playlist if configured
                                            try
                                            {
                                                var playlistName = config.GetValue<string>("YouTube:Playlist:Name");
                                                if (!string.IsNullOrWhiteSpace(playlistName))
                                                {
                                                    var playlistPrivacy = config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted";
                                                    var pid = await ytService.EnsurePlaylistAsync(playlistName, playlistPrivacy, CancellationToken.None);
                                                    if (!string.IsNullOrWhiteSpace(pid))
                                                    {
                                                        await ytService.AddVideoToPlaylistAsync(pid, videoId, CancellationToken.None);
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                watcherLogger.LogError(ex, "[YouTube] Failed to add timelapse to playlist");
                                            }
                                                try
                                                {
                                                    var frameFiles = Directory.GetFiles(Path.GetDirectoryName(createdVideoPath) ?? string.Empty, "frame_*.jpg").OrderBy(f => f).ToArray();
                                                    if (frameFiles.Length > 0)
                                                    {
                                                        var lastFrame = frameFiles[^1];
                                                        var bytes = await File.ReadAllBytesAsync(lastFrame, CancellationToken.None);
                                                        var okThumb = await ytService.SetVideoThumbnailAsync(videoId, bytes, CancellationToken.None);
                                                        if (okThumb)
                                                        {
                                                            watcherLogger.LogInformation("[Timelapse] Set video thumbnail from last frame.");
                                                        }
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    watcherLogger.LogError(ex, "[Timelapse] Failed to set video thumbnail");
                                                }
                                        }
                                }
                                catch (Exception ex)
                                {
                                    watcherLogger.LogError(ex, "[Timelapse] Failed to upload video to YouTube");
                                }
                            }
                            else if (!uploadTimelapse)
                            {
                                    watcherLogger.LogInformation("[Timelapse] Video upload to YouTube is disabled (YouTube:TimelapseUpload:Enabled=false)");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        watcherLogger.LogError(ex, "[Timelapse] Failed to create video");
                    }
                    timelapse.Dispose();
                }
                
                // Cleanup services
                ytService?.Dispose();
                timelapseManager?.Dispose();
                
                watcherLogger.LogInformation("[Watcher] Cleanup complete.");
            }
        }

        // Stream helper moved from Program.cs
    public static async Task StartYouTubeStreamAsync(IConfiguration config, ILoggerFactory loggerFactory, CancellationToken cancellationToken, bool enableTimelapse = true, ITimelapseMetadataProvider? timelapseProvider = null, bool allowYouTube = true)
        {
            var streamLogger = loggerFactory.CreateLogger(nameof(MoonrakerPoller));
            string? rtmpUrl = null;
            string? streamKey = null;
            string? broadcastId = null;
            string? moonrakerFilename = null;
            YouTubeControlService? ytService = null;
            TimelapseService? timelapse = null;
            CancellationTokenSource? timelapseCts = null;
            Task? timelapseTask = null;
            IStreamer? streamer = null;
            OverlayTextService? overlayService = null;

            // Ensure only ONE streamer is running at any time. If a streamer exists, stop it first.
            try
            {
                IStreamer? existing;
                CancellationTokenSource? existingCts;
                lock (_streamLock)
                {
                    existing = _currentStreamer;
                    existingCts = _currentStreamerCts;
                    _currentStreamer = null;
                    _currentStreamerCts = null;
                }
                if (existing != null)
                {
                    try { existingCts?.Cancel(); } catch { }
                    try { await Task.WhenAny(existing.ExitTask, Task.Delay(3000, cancellationToken)); } catch { }
                    try { existing.Stop(); } catch { }
                }

                var oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
                var oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");
                bool hasYouTubeOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);

                // Read source from config
                var source = config.GetValue<string>("Stream:Source");
                var serveEnabled = config.GetValue<bool?>("Serve:Enabled") ?? true;
                // Always use local overlay proxy when web server is enabled - this ensures overlays and camera simulation work
                // and provides consistency for ffmpeg pulling the composited stream over HTTP.
                if (serveEnabled)
                {
                    source = "http://127.0.0.1:8080/stream/overlay";
                    streamLogger.LogInformation("[Stream] Using local overlay proxy stream as ffmpeg source (http://127.0.0.1:8080/stream/overlay)");
                }

                // Respect config flag: whether automatic LiveBroadcast creation is enabled
                var liveBroadcastEnabled = allowYouTube && (config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true);

                // Prepare overlay options only when NOT using pre-composited overlay endpoint
                FfmpegOverlayOptions? overlayOptions = null;
                if (!(config.GetValue<bool?>("Serve:Enabled") ?? true) && (config.GetValue<bool?>("Overlay:Enabled") ?? false))
                {
                    try
                    {
                        overlayService = new OverlayTextService(config, timelapseProvider, null, loggerFactory.CreateLogger<OverlayTextService>());
                        overlayService.Start();
                        overlayOptions = new FfmpegOverlayOptions
                        {
                            TextFile = overlayService.TextFilePath,
                            FontFile = config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
                            FontSize = config.GetValue<int?>("Overlay:FontSize") ?? 22,
                            FontColor = config.GetValue<string>("Overlay:FontColor") ?? "white",
                            Box = config.GetValue<bool?>("Overlay:Box") ?? true,
                            BoxColor = config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4",
                            BoxBorderW = config.GetValue<int?>("Overlay:BoxBorderW") ?? 8,
                            X = config.GetValue<string>("Overlay:X") ?? "(w-tw)-20",
                            Y = config.GetValue<string>("Overlay:Y") ?? "20"
                        };
                        streamLogger.LogInformation("[Overlay] Enabled drawtext overlay from {TextFile}", overlayOptions.TextFile);
                    }
                    catch (Exception ex)
                    {
                        streamLogger.LogError(ex, "[Overlay] Failed to start overlay service");
                        overlayOptions = null;
                    }
                }

                // Validate source (can't stream without an MJPEG source URL)
                if (string.IsNullOrWhiteSpace(source))
                {
                    streamLogger.LogError("Error: Stream:Source is not configured. Aborting stream.");
                    return;
                }

                if (hasYouTubeOAuth && liveBroadcastEnabled)
                {
                    // Check if broadcast already exists before creating
                    if (IsBroadcastActive)
                    {
                        streamLogger.LogInformation("[YouTube] Broadcast already active, using existing broadcast for stream");
                        // Don't create a new broadcast, just use local mode or attach to existing
                        rtmpUrl = null;
                        streamKey = null;
                    }
                    else if (await _broadcastCreationLock.WaitAsync(0, cancellationToken))
                    {
                        try
                        {
                            // Double-check inside the lock
                            if (IsBroadcastActive)
                            {
                                streamLogger.LogInformation("[YouTube] Broadcast became active while waiting for lock, skipping creation");
                                rtmpUrl = null;
                                streamKey = null;
                            }
                            else
                            {
                                streamLogger.LogInformation("[YouTube] Creating live broadcast via OAuth...");
                                var ytLogger = loggerFactory.CreateLogger<YouTubeControlService>();
                    ytService = new YouTubeControlService(config, ytLogger);

                    // Authenticate
                    if (!await ytService.AuthenticateAsync(cancellationToken))
                    {
                        streamLogger.LogWarning("[YouTube] Authentication failed. Starting local stream only.");
                        ytService?.Dispose();
                        ytService = null;
                        rtmpUrl = null;
                        streamKey = null;
                    }
                    else
                    {
                        // Create broadcast and stream
                        var result = await ytService.CreateLiveBroadcastAsync(cancellationToken);
                        if (result.rtmpUrl == null || result.streamKey == null)
                        {
                            streamLogger.LogWarning("[YouTube] Failed to create broadcast. Starting local stream only.");
                            ytService?.Dispose();
                            ytService = null;
                            rtmpUrl = null;
                            streamKey = null;
                        }
                        else
                        {
                            rtmpUrl = result.rtmpUrl;
                            streamKey = result.streamKey;
                            broadcastId = result.broadcastId;
                            moonrakerFilename = result.filename;

                            streamLogger.LogInformation("[YouTube] Broadcast created! Watch at: https://www.youtube.com/watch?v={BroadcastId}", broadcastId);
                            // Publish broadcast state so UI/status endpoint reflects we're live/going live
                            try
                            {
                                lock (_streamLock)
                                {
                                    _currentYouTubeService = ytService;
                                    _currentBroadcastId = broadcastId;
                                }
                            }
                            catch { }
                            // Dump the LiveBroadcast and LiveStream resources for debugging
                            try
                            {
                                await ytService.LogBroadcastAndStreamResourcesAsync(broadcastId, null, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                streamLogger.LogError(ex, "Failed to log broadcast/stream resources");
                            }

                            // Note: Live broadcasts can only be added to playlists after they complete
                            // Playlist addition happens in StopBroadcastAsync after the broadcast ends

                            // Upload initial thumbnail for the broadcast
                            try
                            {
                                streamLogger.LogInformation("[Thumbnail] Capturing initial thumbnail...");
                                var initialThumbnail = await FetchSingleJpegFrameAsync(source, 10, cancellationToken);
                                if (initialThumbnail != null && !string.IsNullOrWhiteSpace(broadcastId))
                                {
                                    var ok = await ytService.SetBroadcastThumbnailAsync(broadcastId, initialThumbnail, cancellationToken);
                                    if (ok)
                                        streamLogger.LogInformation("[Thumbnail] Initial thumbnail uploaded successfully");
                                }
                            }
                            catch (Exception ex)
                            {
                                streamLogger.LogError(ex, "[Thumbnail] Failed to upload initial thumbnail");
                            }

                            // Start timelapse service for stream mode (only if enabled)
                            if (enableTimelapse)
                            {
                                var mainTlDir = config.GetValue<string>("Timelapse:MainFolder") ?? Path.Combine(Directory.GetCurrentDirectory(), "timelapse");
                                // Use filename from Moonraker if available, otherwise use timestamp
                                string streamId;
                                if (!string.IsNullOrWhiteSpace(moonrakerFilename))
                                {
                                    // Use just the filename for consistency with poll mode
                                    var filenameSafe = SanitizeFilename(moonrakerFilename);
                                    streamId = filenameSafe;
                                    streamLogger.LogInformation("[Timelapse] Using filename from Moonraker: {Filename}", moonrakerFilename);
                                }
                                else
                                {
                                    streamId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                                    streamLogger.LogInformation("[Timelapse] No filename from Moonraker, using timestamp only");
                                }
                                timelapse = new TimelapseService(mainTlDir, streamId, loggerFactory.CreateLogger<TimelapseService>());

                                // Capture immediate first frame for timelapse
                                streamLogger.LogInformation("[Timelapse] Capturing initial frame...");
                                try
                                {
                                    var initialFrame = await FetchSingleJpegFrameAsync(source, 10, cancellationToken);
                                    if (initialFrame != null)
                                    {
                                        await timelapse.SaveFrameAsync(initialFrame, cancellationToken);
                                        streamLogger.LogInformation("[Timelapse] Initial frame captured successfully");
                                    }
                                    else
                                    {
                                        streamLogger.LogWarning("[Timelapse] Warning: Failed to capture initial frame");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    streamLogger.LogError(ex, "[Timelapse] Error capturing initial frame");
                                }

                                timelapseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                var timelapsePeriod = config.GetValue<TimeSpan?>("Timelapse:Period") ?? TimeSpan.FromMinutes(1);
                                timelapseTask = Task.Run(async () =>
                                {
                                    while (!timelapseCts.Token.IsCancellationRequested)
                                    {
                                        try
                                        {
                                            var frame = await FetchSingleJpegFrameAsync(source, 10, timelapseCts.Token);
                                            if (frame != null)
                                            {
                                                await timelapse.SaveFrameAsync(frame, timelapseCts.Token);
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            streamLogger.LogError(ex, "Timelapse frame error");
                                        }
                                        await Task.Delay(timelapsePeriod, timelapseCts.Token);
                                    }
                                }, timelapseCts.Token);
                            }
                        }
                    }
                            }
                        }
                        finally
                        {
                            _broadcastCreationLock.Release();
                        }
                    }
                    else
                    {
                            streamLogger.LogInformation("[YouTube] Another broadcast creation is in progress, skipping");
                        rtmpUrl = null;
                        streamKey = null;
                    }
                }
                else
                {
                    // No YouTube OAuth configured or broadcast disabled - run local stream only
                    streamLogger.LogInformation("[Stream] Starting local stream only (no YouTube broadcast)");
                    rtmpUrl = null;
                    streamKey = null;
                }

                // Start streaming with chosen implementation
                string? fullRtmpUrl = null;
                if (!string.IsNullOrWhiteSpace(rtmpUrl) && !string.IsNullOrWhiteSpace(streamKey))
                {
                    fullRtmpUrl = $"{rtmpUrl}/{streamKey}";
                }
                var localStreamEnabled = config.GetValue<bool?>("Stream:Local:Enabled") ?? false;

                var targetFps = config.GetValue<int?>("Stream:TargetFps") ?? 30;
                var bitrateKbps = config.GetValue<int?>("Stream:BitrateKbps") ?? 2500;

                // Determine audio source for ffmpeg when serving locally
                string? audioUrl = null;
                var useApiAudio = config.GetValue<bool?>("Stream:Audio:UseApiStream") ?? true;
                var audioFeatureEnabled = config.GetValue<bool?>("Audio:Enabled") ?? config.GetValue<bool?>("audio:enabled") ?? true;
                if ((config.GetValue<bool?>("Serve:Enabled") ?? true) && useApiAudio && audioFeatureEnabled)
                {
                    audioUrl = config.GetValue<string>("Stream:Audio:Url");
                    if (string.IsNullOrWhiteSpace(audioUrl)) audioUrl = "http://127.0.0.1:8080/api/audio/stream";
                }

                // Always use the ffmpeg-based streamer. If fullRtmpUrl is null, ffmpeg will produce local preview only.
                streamLogger.LogInformation("Starting ffmpeg streamer to {Target} (fps={Fps}, kbps={Kbps})", fullRtmpUrl != null ? rtmpUrl + "/***" : "local preview", targetFps, bitrateKbps);
                streamer = new FfmpegStreamer(source, fullRtmpUrl, targetFps, bitrateKbps, overlayOptions, audioUrl, loggerFactory.CreateLogger<FfmpegStreamer>());
                
                // Store streamer reference so it can be promoted to live later
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_streamLock)
                {
                    _currentStreamer = streamer;
                    _currentStreamerCts = cts;
                }
                
                // Start streamer without awaiting so we can detect ingestion while it's running
                var streamerStartTask = streamer.StartAsync(cts.Token);

                // Ensure streamer is force-stopped when cancellation is requested (extra safety)
                using var stopOnCancel = cancellationToken.Register(() =>
                {
                    try
                    {
                        streamLogger.LogInformation("Cancellation requested — stopping streamer...");
                        streamer?.Stop();
                    }
                    catch (Exception ex)
                    {
                        streamLogger.LogError(ex, "Error stopping streamer on cancel");
                    }
                });

                // If we created a broadcast, transition it to live
                    if (ytService != null && broadcastId != null)
                {
                    streamLogger.LogInformation("Stream started, waiting for YouTube ingestion to become active before transitioning to live...");
                    // Wait up to 90s for ingestion to be detected by YouTube
                    var ingestionOk = await ytService.WaitForIngestionAsync(null, TimeSpan.FromSeconds(90), cancellationToken);
                    if (!ingestionOk)
                    {
                        streamLogger.LogWarning("Warning: ingestion not active. Attempting transition anyway (may fail)...");
                    }
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await ytService.TransitionBroadcastToLiveWhenReadyAsync(broadcastId, TimeSpan.FromSeconds(90), 3, cancellationToken);
                    }
                    else
                    {
                        streamLogger.LogInformation("Cancellation requested before transition; skipping TransitionBroadcastToLive.");
                    }

                    // If the initial transition didn't succeed because the camera/feed wasn't yet available,
                    // keep monitoring ingestion and retry transitioning until either we succeed or the streamer stops.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Read monitor settings from config (fallback to defaults)
                            var monitorRetrySeconds = config.GetValue<int?>("YouTube:Monitor:RetrySeconds") ?? 30;
                            var ingestionWaitSeconds = config.GetValue<int?>("YouTube:Monitor:IngestionWaitSeconds") ?? 60;

                            streamLogger.LogInformation("[YouTubeMonitor] Starting background monitor to retry transition if ingestion becomes active later...");
                            while (!cancellationToken.IsCancellationRequested)
                            {
                                try
                                {
                                    _isWaitingForIngestion = true;

                                    // First, try transitioning immediately (this will be treated as success if already live)
                                        try
                                        {
                                            var tried = await ytService.TransitionBroadcastToLiveAsync(broadcastId, cancellationToken);
                                            streamLogger.LogDebug("[YouTubeMonitor] Immediate transition attempt result: {Tried}", tried);
                                            if (tried)
                                            {
                                                streamLogger.LogInformation("[YouTubeMonitor] Transition succeeded (or already live); stopping monitor.");
                                                break;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            streamLogger.LogError(ex, "[YouTubeMonitor] Immediate transition attempt failed");
                                        }

                                    // Wait for ingestion to become active, then try transition
                                    var ok = await ytService.WaitForIngestionAsync(null, TimeSpan.FromSeconds(ingestionWaitSeconds), cancellationToken);
                                    if (ok)
                                    {
                                        streamLogger.LogInformation("[YouTubeMonitor] Ingestion active; attempting transition to live...");
                                        try
                                        {
                                            var tOk = await ytService.TransitionBroadcastToLiveAsync(broadcastId, cancellationToken);
                                            streamLogger.LogDebug("[YouTubeMonitor] Transition attempt result: {Result}", tOk);
                                            if (tOk) break;
                                        }
                                        catch (Exception ex)
                                        {
                                            streamLogger.LogError(ex, "[YouTubeMonitor] Transition attempt failed");
                                        }
                                    }
                                }
                                catch (OperationCanceledException) { break; }
                                catch (Exception ex)
                                {
                                    streamLogger.LogError(ex, "[YouTubeMonitor] Monitor error");
                                }
                                finally
                                {
                                    _isWaitingForIngestion = false;
                                }

                                // Sleep before retrying (configurable)
                                try { await Task.Delay(TimeSpan.FromSeconds(monitorRetrySeconds), cancellationToken); } catch (OperationCanceledException) { break; }
                            }
                        }
                        catch (Exception ex)
                        {
                            streamLogger.LogError(ex, "[YouTubeMonitor] Background monitor failed");
                        }
                        finally
                        {
                            _isWaitingForIngestion = false;
                        }
                    }, cancellationToken);
                }

                // Wait for the stream to end (started earlier)
                await streamer.ExitTask;
            }
            catch (OperationCanceledException)
            {
                streamLogger.LogInformation("Stream canceled.");
            }
            catch (Exception ex)
            {
                streamLogger.LogError(ex, "Stream error");
            }
            finally
            {
                // Clear streamer reference
                lock (_streamLock)
                {
                    _currentStreamer = null;
                    _currentStreamerCts = null;
                }
                
                try { overlayService?.Dispose(); } catch { }
                // Final thumbnail upload removed: do not capture/upload a final thumbnail automatically
                
                // Stop timelapse task and create video (only if enabled)
                if (enableTimelapse)
                {
                    if (timelapseCts != null)
                    {
                        try { timelapseCts.Cancel(); } catch { }
                        if (timelapseTask != null)
                        {
                            try { await timelapseTask; } catch (OperationCanceledException) { /* Expected */ }
                        }
                        timelapseCts = null;
                        timelapseTask = null;
                    }
                        if (timelapse != null)
                        {
                        streamLogger.LogInformation("[Timelapse] Creating video from {OutputDir}...", timelapse.OutputDir);
                        var folderName = Path.GetFileName(timelapse.OutputDir);
                        var videoPath = Path.Combine(timelapse.OutputDir, $"{folderName}.mp4");
                        // Use a new cancellation token for video creation (don't use the cancelled one)
                        try
                        {
                            var createdVideoPath = await timelapse.CreateVideoAsync(videoPath, 30, CancellationToken.None);

                            // Upload the timelapse video to YouTube if enabled and video was created successfully
                            if (!string.IsNullOrWhiteSpace(createdVideoPath) && File.Exists(createdVideoPath))
                            {
                                var uploadTimelapse = config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false;
                                if (uploadTimelapse && ytService != null)
                                {
                                    streamLogger.LogInformation("[Timelapse] Uploading timelapse video to YouTube...");
                                    try
                                    {
                                        // Use moonrakerFilename if available (from CreateLiveBroadcastAsync), otherwise extract from timelapse folder name
                                        var filenameForUpload = moonrakerFilename ?? Path.GetFileName(timelapse?.OutputDir);
                                        var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, filenameForUpload, CancellationToken.None);
                                        if (!string.IsNullOrWhiteSpace(videoId))
                                        {
                                            streamLogger.LogInformation("[Timelapse] Video uploaded successfully! https://www.youtube.com/watch?v={VideoId}", videoId);
                                            try
                                            {
                                                // Attempt to set video thumbnail to last timelapse frame
                                                var frameFiles = Directory.GetFiles(Path.GetDirectoryName(createdVideoPath) ?? string.Empty, "frame_*.jpg").OrderBy(f => f).ToArray();
                                                if (frameFiles.Length > 0)
                                                {
                                                    var lastFrame = frameFiles[^1];
                                                    var bytes = await File.ReadAllBytesAsync(lastFrame, CancellationToken.None);
                                                    var okThumb = await ytService.SetVideoThumbnailAsync(videoId, bytes, CancellationToken.None);
                                                    if (okThumb)
                                                    {
                                                        streamLogger.LogInformation("[Timelapse] Set video thumbnail from last frame.");
                                                    }
                                                }
                                            }
                                            catch (Exception thx)
                                            {
                                                streamLogger.LogError(thx, "[Timelapse] Failed to set video thumbnail");
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        streamLogger.LogError(ex, "[Timelapse] Failed to upload video to YouTube");
                                    }
                                }
                                else if (!uploadTimelapse)
                                {
                                    streamLogger.LogInformation("[Timelapse] Video upload to YouTube is disabled (YouTube:TimelapseUpload:Enabled=false)");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            streamLogger.LogError(ex, "[Timelapse] Failed to create video");
                        }
                        timelapse?.Dispose();
                        timelapse = null;
                    }
                }
                // Ensure streamer is stopped (in case cancellation didn't trigger it for some reason)
                try { streamer?.Stop(); } catch { }

                // Clean up YouTube broadcast if created
                if (ytService != null && broadcastId != null)
                {
                    streamLogger.LogInformation("Ending YouTube broadcast...");
                    try
                    {
                        await ytService.EndBroadcastAsync(broadcastId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        streamLogger.LogError(ex, "Failed to end broadcast");
                    }
                    finally
                    {
                        lock (_streamLock)
                        {
                            _currentBroadcastId = null;
                            _currentYouTubeService = null;
                        }
                    }
                }

                ytService?.Dispose();
            }
        }

        // Utility: Extract a single JPEG frame from MJPEG stream URL (moved from Program.cs)
        private static async Task<byte[]?> FetchSingleJpegFrameAsync(string mjpegUrl, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            using var resp = await client.GetAsync(mjpegUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[64 * 1024];
            using var ms = new MemoryStream();
            int bytesRead;
            // Read until we find a JPEG frame
            while (!cancellationToken.IsCancellationRequested)
            {
                bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (bytesRead == 0) break;
                ms.Write(buffer, 0, bytesRead);
                if (MjpegReader.TryExtractJpeg(ms, out var jpegBytes) && jpegBytes != null)
                {
                    return jpegBytes;
                }
            }
            return null;
        }

        // Utility: Sanitize filename for use as folder name (moved from Program.cs)
        private static string SanitizeFilename(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                return "unknown";
            
            // Remove file extension
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
            
            // Define characters to remove or replace
            var invalidChars = Path.GetInvalidFileNameChars()
                .Concat(Path.GetInvalidPathChars())
                .Concat(new[] { ' ', '-', '(', ')', '[', ']', '{', '}', ':', ';', ',', '.', '#' })
                .Distinct()
                .ToArray();
            
            var result = nameWithoutExtension;
            
            // Replace invalid characters
            foreach (var c in invalidChars)
            {
                result = result.Replace(c, '_');
            }
            
            // Special replacements
            result = result.Replace("&", "and");
            
            // Clean up multiple underscores
            while (result.Contains("__"))
            {
                result = result.Replace("__", "_");
            }
            
            // Trim underscores from start and end
            result = result.Trim('_');
            
            // Ensure we have a valid result
            return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
        }
    }
}
