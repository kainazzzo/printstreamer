using PrintStreamer.Streamers;
using PrintStreamer.Timelapse;
using PrintStreamer.Overlay;
using PrintStreamer.Utils;
using PrintStreamer.Interfaces;

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
        public static void RestartCurrentStreamerWithConfig(IConfiguration config)
        {
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
                            await StartYouTubeStreamAsync(config, cts.Token, enableTimelapse: false, timelapseProvider: null, allowYouTube: false);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[RestartStreamer] Error starting new streamer: {ex.Message}");
                        }
                    }, cts.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RestartStreamer] Failed to restart streamer: {ex.Message}");
                }
            });
        }

        public static bool IsBroadcastActive => !string.IsNullOrWhiteSpace(_currentBroadcastId);
        public static string? CurrentBroadcastId => _currentBroadcastId;
    public static bool IsStreamerRunning => _currentStreamer != null;
    public static bool IsWaitingForIngestion => _isWaitingForIngestion;

        /// <summary>
        /// Promote the currently-running encoder (HLS-only) to a YouTube live broadcast by creating
        /// the broadcast resources and restarting the ffmpeg process to include RTMP output.
        /// Returns true on success.
        /// </summary>
        public static async Task<(bool ok, string? message, string? broadcastId)> StartBroadcastAsync(IConfiguration config, CancellationToken cancellationToken)
        {
            // Use semaphore to prevent concurrent broadcast creation
            if (!await _broadcastCreationLock.WaitAsync(0, cancellationToken))
            {
                Console.WriteLine("[StartBroadcast] Another broadcast creation is already in progress, skipping duplicate");
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
                    Console.WriteLine("[StartBroadcast] A broadcast is already active, skipping duplicate creation");
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

            // In some flows the local HLS streamer may be restarting (e.g., camera toggle or prior stop).
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
            var yt = new YouTubeControlService(config);
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

            // Restart streamer if one is running: cancel current, then start a new one with RTMP+HLS
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

            // Start new ffmpeg streamer with RTMP and HLS
            try
            {
                var localStreamEnabled = config.GetValue<bool?>("Stream:Local:Enabled") ?? false;
                var hlsFolder = config.GetValue<string>("Stream:Local:HlsFolder") ?? Path.Combine(Directory.GetCurrentDirectory(), "hls");
                var targetFps = config.GetValue<int?>("Stream:TargetFps") ?? 6;
                var bitrateKbps = config.GetValue<int?>("Stream:BitrateKbps") ?? 800;

                // Determine audio source for ffmpeg when serving locally
                string? audioUrl = null;
                var useApiAudio = config.GetValue<bool?>("Stream:Audio:UseApiStream") ?? true;
                var audioFeatureEnabled = config.GetValue<bool?>("Audio:Enabled") ?? config.GetValue<bool?>("audio:enabled") ?? true;
                if ((config.GetValue<bool?>("Serve:Enabled") ?? true) && useApiAudio && audioFeatureEnabled)
                {
                    audioUrl = config.GetValue<string>("Stream:Audio:Url");
                    if (string.IsNullOrWhiteSpace(audioUrl)) audioUrl = "http://127.0.0.1:8080/api/audio/stream";
                }

                

                // Overlay options
                FfmpegOverlayOptions? overlayOptions = null;
                if (config.GetValue<bool?>("Overlay:Enabled") ?? false)
                {
                    var overlayService = new OverlayTextService(config, null, null);
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
                    Console.WriteLine("[Stream] Using local overlay proxy stream as ffmpeg source (http://127.0.0.1:8080/stream/overlay)");
                }
                if (string.IsNullOrWhiteSpace(source))
                {
                    yt.Dispose();
                    return (false, "Stream:Source is not configured", null);
                }
                var streamer = new FfmpegStreamer(source, newRtmp + "/" + newKey, targetFps, bitrateKbps, overlayOptions, localStreamEnabled ? hlsFolder : null, audioUrl);
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
                            Console.WriteLine("[YouTube] Waiting for ingestion and attempting to transition broadcast to live...");
                            var ok = await yt.TransitionBroadcastToLiveWhenReadyAsync(newBroadcastId!, TimeSpan.FromSeconds(120), 5, CancellationToken.None);
                            Console.WriteLine($"[YouTube] Transition to live {(ok ? "succeeded" : "failed")} for broadcast {newBroadcastId}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[YouTube] Error while transitioning broadcast to live: {ex.Message}");
                        }
                    }, CancellationToken.None);
                }
                else
                {
                    Console.WriteLine("[YouTube] No broadcastId available; skipping automatic transition to live.");
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
            Console.WriteLine($"[StartBroadcast] Unexpected error: {outerEx.Message}");
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
        public static async Task<(bool ok, string? message)> StopBroadcastAsync(IConfiguration config, CancellationToken cancellationToken)
        {
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
                        Console.WriteLine($"[YouTube] Ended broadcast {broadcastId}");
                        
                        // Add the completed broadcast to the playlist
                        try
                        {
                            // Wait a moment for YouTube to process the broadcast completion
                            await Task.Delay(2000, cancellationToken);
                            
                            var playlistName = config.GetValue<string>("YouTube:Playlist:Name");
                            if (!string.IsNullOrWhiteSpace(playlistName))
                            {
                                Console.WriteLine($"[YouTube] Adding completed broadcast to playlist '{playlistName}'...");
                                var playlistPrivacy = config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted";
                                var pid = await yt.EnsurePlaylistAsync(playlistName, playlistPrivacy, cancellationToken);
                                if (!string.IsNullOrWhiteSpace(pid))
                                {
                                    var added = await yt.AddVideoToPlaylistAsync(pid, broadcastId, cancellationToken);
                                    if (added)
                                    {
                                        Console.WriteLine($"[YouTube] Successfully added broadcast to playlist '{playlistName}'");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[YouTube] Failed to add broadcast to playlist: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[YouTube] Error ending broadcast: {ex.Message}");
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
                Console.WriteLine($"[StopBroadcast] Error: {ex.Message}");
                return (false, ex.Message);
            }
        }

        // Entry point moved from Program.cs
    public static async Task PollAndStreamJobsAsync(IConfiguration config, CancellationToken cancellationToken)
        {
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
                timelapseManager = new TimelapseManager(config);
                
                // Initialize YouTube service if credentials are provided (for timelapse upload)
                var oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
                var oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");
                bool useOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);
                var liveBroadcastEnabled = config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true;
                if (useOAuth && liveBroadcastEnabled)
                {
                    ytService = new YouTubeControlService(config);
                    var authOk = await ytService.AuthenticateAsync(cancellationToken);
                    if (!authOk)
                    {
                        Console.WriteLine("[Watcher] YouTube authentication failed. Timelapse upload will be disabled.");
                        ytService.Dispose();
                        ytService = null;
                    }
                    else
                    {
                        Console.WriteLine("[Watcher] YouTube authenticated successfully for timelapse uploads.");
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

                    Console.WriteLine($"[Watcher] Poll result - Filename: '{currentJob}', State: '{state}', Progress: {progressPct?.ToString("F1") ?? "n/a"}%, Remaining: {remaining?.ToString() ?? "n/a"}, Layer: {currentLayer?.ToString() ?? "n/a"}/{totalLayers?.ToString() ?? "n/a"}");

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
                        Console.WriteLine($"[Watcher] Failed to notify timelapse manager of print progress: {ex.Message}");
                    }

                    // Track if a stream is already active
                    var streamingActive = streamCts != null && streamTask != null && !streamTask.IsCompleted;

                    // Start stream when actively printing (even if filename is missing initially)
                    if (isPrinting && !streamingActive && (string.IsNullOrWhiteSpace(currentJob) || currentJob != lastJobFilename))
                    {
                        // New job detected, start stream and timelapse
                        Console.WriteLine($"[Watcher] New print job detected: {currentJob ?? "(unknown)"}");
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
                                await StartYouTubeStreamAsync(config, streamCts.Token, enableTimelapse: false, timelapseProvider: null, allowYouTube: !alreadyBroadcasting);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Watcher] Stream error: {ex.Message}");
                            }
                        }, streamCts.Token);
                        // Start timelapse using TimelapseManager (will download G-code and cache metadata)
                        {
                            var jobNameSafe = !string.IsNullOrWhiteSpace(currentJob) ? SanitizeFilename(currentJob) : $"printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                            Console.WriteLine($"[Watcher] Starting timelapse session: {jobNameSafe}");
                            Console.WriteLine($"[Watcher]   - currentJob: '{currentJob}'");
                            Console.WriteLine($"[Watcher]   - jobNameSafe: '{jobNameSafe}'");
                            
                            // Start timelapse via manager (downloads G-code, caches metadata, captures initial frame)
                            activeTimelapseSessionName = await timelapseManager!.StartTimelapseAsync(jobNameSafe, currentJob);
                            if (activeTimelapseSessionName != null)
                            {
                                Console.WriteLine($"[Watcher] Timelapse session started: {activeTimelapseSessionName}");
                            }
                            else
                            {
                                Console.WriteLine($"[Watcher] Warning: Failed to start timelapse session");
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
                            Console.WriteLine($"[Timelapse] *** Last-layer detected ***");
                            Console.WriteLine($"[Timelapse]   Remaining time: {remaining?.ToString() ?? "n/a"} (threshold: {thresholdSecs}s, triggered: {lastLayerByTime})");
                            Console.WriteLine($"[Timelapse]   Progress: {progressPct?.ToString("F1") ?? "n/a"}% (threshold: {thresholdPct}%, triggered: {lastLayerByProgress})");
                            Console.WriteLine($"[Timelapse]   Layer: {currentLayer?.ToString() ?? "n/a"}/{totalLayers?.ToString() ?? "n/a"} (threshold: -{layerThreshold}, triggered: {lastLayerByLayer})");
                            Console.WriteLine($"[Timelapse] Capturing final frame and finalizing timelapse now...");
                            lastLayerTriggered = true;

                            // Stop timelapse via manager and kick off finalize/upload in the background
                            var sessionToFinalize = activeTimelapseSessionName;
                            var uploadEnabled = config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false;
                            timelapseFinalizeTask = Task.Run(async () =>
                            {
                                try
                                {
                                    Console.WriteLine($"[Timelapse] Stopping timelapse session (early finalize): {sessionToFinalize}");
                                    var createdVideoPath = await timelapseManager!.StopTimelapseAsync(sessionToFinalize!);

                                    if (!string.IsNullOrWhiteSpace(createdVideoPath) && File.Exists(createdVideoPath) && uploadEnabled && ytService != null)
                                    {
                                        try
                                        {
                                            Console.WriteLine("[Timelapse] Uploading timelapse video (early finalize) to YouTube...");
                                            var titleName = lastJobFilename ?? sessionToFinalize;
                                            var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, titleName!, CancellationToken.None);
                                            if (!string.IsNullOrWhiteSpace(videoId))
                                            {
                                                Console.WriteLine($"[Timelapse] Early-upload complete: https://www.youtube.com/watch?v={videoId}");
                                            }
                                        }
                                        catch (Exception upx)
                                        {
                                            Console.WriteLine($"[Timelapse] Early-upload failed: {upx.Message}");
                                        }
                                    }
                                }
                                catch (Exception fex)
                                {
                                    Console.WriteLine($"[Timelapse] Early finalize failed: {fex.Message}");
                                }
                            }, CancellationToken.None);

                            // Clear the active session reference so end-of-print path won't double-run
                            activeTimelapseSessionName = null;
                        }
                    }
                    else if (!isPrinting && (streamCts != null || streamTask != null))
                    {
                        // Job finished, end stream and finalize timelapse
                        Console.WriteLine($"[Watcher] Print job finished: {lastJobFilename}");
                        // Preserve the filename we detected at job start for upload metadata
                        var finishedJobFilename = lastJobFilename;
                        lastCompletedJobFilename = finishedJobFilename;
                        lastJobFilename = null;
                        
                        // Properly stop the broadcast (ends YouTube stream and cleans up)
                        try
                        {
                            var (ok, msg) = await StopBroadcastAsync(config, CancellationToken.None);
                            if (ok)
                            {
                                Console.WriteLine("[Watcher] Broadcast stopped successfully");
                            }
                            else
                            {
                                Console.WriteLine($"[Watcher] Error stopping broadcast: {msg}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Watcher] Exception stopping broadcast: {ex.Message}");
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
                            Console.WriteLine($"[Timelapse] Stopping timelapse session (end of print): {activeTimelapseSessionName}");
                            try
                            {
                                var createdVideoPath = await timelapseManager!.StopTimelapseAsync(activeTimelapseSessionName);
                                
                                // Upload the timelapse video to YouTube if enabled and video was created successfully
                                if (!string.IsNullOrWhiteSpace(createdVideoPath) && File.Exists(createdVideoPath))
                                {
                                    var uploadTimelapse = config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false;
                                    if (uploadTimelapse && ytService != null)
                                    {
                                        Console.WriteLine("[Timelapse] Uploading timelapse video to YouTube...");
                                        try
                                        {
                                            // Use the timelapse folder name (sanitized) for nicer titles
                                            var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, activeTimelapseSessionName, CancellationToken.None);
                                            if (!string.IsNullOrWhiteSpace(videoId))
                                            {
                                                Console.WriteLine($"[Timelapse] Video uploaded successfully! https://www.youtube.com/watch?v={videoId}");
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
                                                    Console.WriteLine($"[YouTube] Failed to add timelapse to playlist: {ex.Message}");
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
                                                                Console.WriteLine("[Timelapse] Set video thumbnail from last frame.");
                                                            }
                                                        }
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        Console.WriteLine($"[Timelapse] Failed to set video thumbnail: {ex.Message}");
                                                    }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[Timelapse] Failed to upload video to YouTube: {ex.Message}");
                                        }
                                    }
                                    else if (!uploadTimelapse)
                                    {
                                        Console.WriteLine("[Timelapse] Video upload to YouTube is disabled (YouTube:TimelapseUpload:Enabled=false)");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Timelapse] Failed to create video: {ex.Message}");
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
                            Console.WriteLine($"[Watcher] Using fast polling ({pollInterval.TotalSeconds}s) - near completion");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Watcher] Error: {ex.Message}");
                }

                await Task.Delay(pollInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[Watcher] Polling cancelled by user.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Watcher] Unexpected error: {ex.Message}");
            }
            finally
            {
                // Cleanup on exit
                Console.WriteLine("[Watcher] Shutting down...");
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
                    Console.WriteLine($"[Timelapse] Creating video from {timelapse.OutputDir}...");
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
                                Console.WriteLine("[Timelapse] Uploading timelapse video to YouTube...");
                                try
                                {
                                    // Prefer the recently finished job's filename; fallback to timelapse folder name
                                    var filenameForUpload = lastCompletedJobFilename ?? lastJobFilename ?? folderName;
                                    var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, filenameForUpload, CancellationToken.None);
                                        if (!string.IsNullOrWhiteSpace(videoId))
                                        {
                                            Console.WriteLine($"[Timelapse] Video uploaded successfully! https://www.youtube.com/watch?v={videoId}");
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
                                                Console.WriteLine($"[YouTube] Failed to add timelapse to playlist: {ex.Message}");
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
                                                            Console.WriteLine("[Timelapse] Set video thumbnail from last frame.");
                                                        }
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    Console.WriteLine($"[Timelapse] Failed to set video thumbnail: {ex.Message}");
                                                }
                                        }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[Timelapse] Failed to upload video to YouTube: {ex.Message}");
                                }
                            }
                            else if (!uploadTimelapse)
                            {
                                Console.WriteLine("[Timelapse] Video upload to YouTube is disabled (YouTube:TimelapseUpload:Enabled=false)");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Timelapse] Failed to create video: {ex.Message}");
                    }
                    timelapse.Dispose();
                }
                
                // Cleanup services
                ytService?.Dispose();
                timelapseManager?.Dispose();
                
                Console.WriteLine("[Watcher] Cleanup complete.");
            }
        }

        // Stream helper moved from Program.cs
    public static async Task StartYouTubeStreamAsync(IConfiguration config, CancellationToken cancellationToken, bool enableTimelapse = true, ITimelapseMetadataProvider? timelapseProvider = null, bool allowYouTube = true)
        {
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
                    Console.WriteLine("[Stream] Using local overlay proxy stream as ffmpeg source (http://127.0.0.1:8080/stream/overlay)");
                }

                // Respect config flag: whether automatic LiveBroadcast creation is enabled
                var liveBroadcastEnabled = allowYouTube && (config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true);

                // Prepare overlay options early so native fallback can reuse them
                FfmpegOverlayOptions? overlayOptions = null;
                if (config.GetValue<bool?>("Overlay:Enabled") ?? false)
                {
                    try
                    {
                        overlayService = new OverlayTextService(config, timelapseProvider, null);
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
                        Console.WriteLine($"[Overlay] Enabled drawtext overlay from {overlayOptions.TextFile}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Overlay] Failed to start overlay service: {ex.Message}");
                        overlayOptions = null;
                    }
                }

                // Validate source (can't stream without an MJPEG source URL)
                if (string.IsNullOrWhiteSpace(source))
                {
                    Console.WriteLine("Error: Stream:Source is not configured. Aborting stream.");
                    return;
                }

                if (hasYouTubeOAuth && liveBroadcastEnabled)
                {
                    // Check if broadcast already exists before creating
                    if (IsBroadcastActive)
                    {
                        Console.WriteLine("[YouTube] Broadcast already active, using existing broadcast for stream");
                        // Don't create a new broadcast, just use HLS-only mode or attach to existing
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
                                Console.WriteLine("[YouTube] Broadcast became active while waiting for lock, skipping creation");
                                rtmpUrl = null;
                                streamKey = null;
                            }
                            else
                            {
                                Console.WriteLine("[YouTube] Creating live broadcast via OAuth...");
                                ytService = new YouTubeControlService(config);

                    // Authenticate
                    if (!await ytService.AuthenticateAsync(cancellationToken))
                    {
                        Console.WriteLine("[YouTube] Authentication failed. Starting local HLS-only stream.");
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
                            Console.WriteLine("[YouTube] Failed to create broadcast. Starting local HLS-only stream.");
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

                            Console.WriteLine($"[YouTube] Broadcast created! Watch at: https://www.youtube.com/watch?v={broadcastId}");
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
                                Console.WriteLine($"Failed to log broadcast/stream resources: {ex.Message}");
                            }

                            // Note: Live broadcasts can only be added to playlists after they complete
                            // Playlist addition happens in StopBroadcastAsync after the broadcast ends

                            // Upload initial thumbnail for the broadcast
                            try
                            {
                                Console.WriteLine("[Thumbnail] Capturing initial thumbnail...");
                                var initialThumbnail = await FetchSingleJpegFrameAsync(source, 10, cancellationToken);
                                if (initialThumbnail != null && !string.IsNullOrWhiteSpace(broadcastId))
                                {
                                    var ok = await ytService.SetBroadcastThumbnailAsync(broadcastId, initialThumbnail, cancellationToken);
                                    if (ok)
                                        Console.WriteLine($"[Thumbnail] Initial thumbnail uploaded successfully");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Thumbnail] Failed to upload initial thumbnail: {ex.Message}");
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
                                    Console.WriteLine($"[Timelapse] Using filename from Moonraker: {moonrakerFilename}");
                                }
                                else
                                {
                                    streamId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                                    Console.WriteLine($"[Timelapse] No filename from Moonraker, using timestamp only");
                                }
                                timelapse = new TimelapseService(mainTlDir, streamId);

                                // Capture immediate first frame for timelapse
                                Console.WriteLine($"[Timelapse] Capturing initial frame...");
                                try
                                {
                                    var initialFrame = await FetchSingleJpegFrameAsync(source, 10, cancellationToken);
                                    if (initialFrame != null)
                                    {
                                        await timelapse.SaveFrameAsync(initialFrame, cancellationToken);
                                        Console.WriteLine($"[Timelapse] Initial frame captured successfully");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[Timelapse] Warning: Failed to capture initial frame");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[Timelapse] Error capturing initial frame: {ex.Message}");
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
                                            Console.WriteLine($"Timelapse frame error: {ex.Message}");
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
                        Console.WriteLine("[YouTube] Another broadcast creation is in progress, skipping");
                        rtmpUrl = null;
                        streamKey = null;
                    }
                }
                else
                {
                    // No YouTube OAuth configured or broadcast disabled - run local HLS-only stream
                    Console.WriteLine("[Stream] Starting local HLS-only stream (no YouTube broadcast)");
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
                var hlsFolder = config.GetValue<string>("Stream:Local:HlsFolder");

                var targetFps = config.GetValue<int?>("Stream:TargetFps") ?? 6;
                var bitrateKbps = config.GetValue<int?>("Stream:BitrateKbps") ?? 800;

                // Determine audio source for ffmpeg when serving locally
                string? audioUrl = null;
                var useApiAudio = config.GetValue<bool?>("Stream:Audio:UseApiStream") ?? true;
                var audioFeatureEnabled = config.GetValue<bool?>("Audio:Enabled") ?? config.GetValue<bool?>("audio:enabled") ?? true;
                if ((config.GetValue<bool?>("Serve:Enabled") ?? true) && useApiAudio && audioFeatureEnabled)
                {
                    audioUrl = config.GetValue<string>("Stream:Audio:Url");
                    if (string.IsNullOrWhiteSpace(audioUrl)) audioUrl = "http://127.0.0.1:8080/api/audio/stream";
                }

                // Always use the ffmpeg-based streamer. If fullRtmpUrl is null, ffmpeg will produce HLS-only preview.
                Console.WriteLine($"Starting ffmpeg streamer to {(fullRtmpUrl != null ? rtmpUrl + "/***" : "HLS-only")} (fps={targetFps}, kbps={bitrateKbps})");
                streamer = new FfmpegStreamer(source, fullRtmpUrl, targetFps, bitrateKbps, overlayOptions, localStreamEnabled ? hlsFolder : null, audioUrl);
                
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
                        Console.WriteLine("Cancellation requested — stopping streamer...");
                        streamer?.Stop();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error stopping streamer on cancel: {ex.Message}");
                    }
                });

                // If we created a broadcast, transition it to live
                    if (ytService != null && broadcastId != null)
                {
                    Console.WriteLine("Stream started, waiting for YouTube ingestion to become active before transitioning to live...");
                    // Wait up to 90s for ingestion to be detected by YouTube
                    var ingestionOk = await ytService.WaitForIngestionAsync(null, TimeSpan.FromSeconds(90), cancellationToken);
                    if (!ingestionOk)
                    {
                        Console.WriteLine("Warning: ingestion not active. Attempting transition anyway (may fail)...");
                    }
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await ytService.TransitionBroadcastToLiveWhenReadyAsync(broadcastId, TimeSpan.FromSeconds(90), 3, cancellationToken);
                    }
                    else
                    {
                        Console.WriteLine("Cancellation requested before transition; skipping TransitionBroadcastToLive.");
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

                            Console.WriteLine("[YouTubeMonitor] Starting background monitor to retry transition if ingestion becomes active later...");
                            while (!cancellationToken.IsCancellationRequested)
                            {
                                try
                                {
                                    _isWaitingForIngestion = true;

                                    // First, try transitioning immediately (this will be treated as success if already live)
                                    try
                                    {
                                        var tried = await ytService.TransitionBroadcastToLiveAsync(broadcastId, cancellationToken);
                                        Console.WriteLine($"[YouTubeMonitor] Immediate transition attempt result: {tried}");
                                        if (tried)
                                        {
                                            Console.WriteLine("[YouTubeMonitor] Transition succeeded (or already live); stopping monitor.");
                                            break;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[YouTubeMonitor] Immediate transition attempt failed: {ex.Message}");
                                    }

                                    // Wait for ingestion to become active, then try transition
                                    var ok = await ytService.WaitForIngestionAsync(null, TimeSpan.FromSeconds(ingestionWaitSeconds), cancellationToken);
                                    if (ok)
                                    {
                                        Console.WriteLine("[YouTubeMonitor] Ingestion active; attempting transition to live...");
                                        try
                                        {
                                            var tOk = await ytService.TransitionBroadcastToLiveAsync(broadcastId, cancellationToken);
                                            Console.WriteLine($"[YouTubeMonitor] Transition attempt result: {tOk}");
                                            if (tOk) break;
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[YouTubeMonitor] Transition attempt failed: {ex.Message}");
                                        }
                                    }
                                }
                                catch (OperationCanceledException) { break; }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[YouTubeMonitor] Monitor error: {ex.Message}");
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
                            Console.WriteLine($"[YouTubeMonitor] Background monitor failed: {ex.Message}");
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
                Console.WriteLine("Stream canceled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Stream error: {ex.Message}");
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
                        Console.WriteLine($"[Timelapse] Creating video from {timelapse.OutputDir}...");
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
                                    Console.WriteLine("[Timelapse] Uploading timelapse video to YouTube...");
                                    try
                                    {
                                        // Use moonrakerFilename if available (from CreateLiveBroadcastAsync), otherwise extract from timelapse folder name
                                        var filenameForUpload = moonrakerFilename ?? Path.GetFileName(timelapse?.OutputDir);
                                        var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, filenameForUpload, CancellationToken.None);
                                        if (!string.IsNullOrWhiteSpace(videoId))
                                        {
                                            Console.WriteLine($"[Timelapse] Video uploaded successfully! https://www.youtube.com/watch?v={videoId}");
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
                                                        Console.WriteLine("[Timelapse] Set video thumbnail from last frame.");
                                                    }
                                                }
                                            }
                                            catch (Exception thx)
                                            {
                                                Console.WriteLine($"[Timelapse] Failed to set video thumbnail: {thx.Message}");
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[Timelapse] Failed to upload video to YouTube: {ex.Message}");
                                    }
                                }
                                else if (!uploadTimelapse)
                                {
                                    Console.WriteLine("[Timelapse] Video upload to YouTube is disabled (YouTube:TimelapseUpload:Enabled=false)");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Timelapse] Failed to create video: {ex.Message}");
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
                    Console.WriteLine("Ending YouTube broadcast...");
                    try
                    {
                        await ytService.EndBroadcastAsync(broadcastId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to end broadcast: {ex.Message}");
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
