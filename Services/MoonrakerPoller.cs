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

        public static bool IsBroadcastActive => !string.IsNullOrWhiteSpace(_currentBroadcastId);

        /// <summary>
        /// Promote the currently-running encoder (HLS-only) to a YouTube live broadcast by creating
        /// the broadcast resources and restarting the ffmpeg process to include RTMP output.
        /// Returns true on success.
        /// </summary>
        public static async Task<(bool ok, string? message)> StartBroadcastAsync(IConfiguration config, CancellationToken cancellationToken)
        {
            // Only supports OAuth flow for automatic broadcast creation
            var oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
            var oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");
            bool useOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);
            if (!useOAuth)
            {
                return (false, "OAuth client credentials not configured");
            }

            lock (_streamLock)
            {
                if (_currentStreamer == null)
                {
                    return (false, "No running encoder to promote");
                }
            }

            // Authenticate and create broadcast
            var yt = new YouTubeControlService(config);
            if (!await yt.AuthenticateAsync(cancellationToken))
            {
                yt.Dispose();
                return (false, "YouTube authentication failed");
            }

            var res = await yt.CreateLiveBroadcastAsync(cancellationToken);
            if (res.rtmpUrl == null || res.streamKey == null)
            {
                yt.Dispose();
                return (false, "Failed to create YouTube broadcast");
            }

            var newRtmp = res.rtmpUrl;
            var newKey = res.streamKey;
            var newBroadcastId = res.broadcastId;

            // Restart streamer: cancel current, then start a new one with RTMP+HLS
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
                // Stop the old streamer
                try { oldCts?.Cancel(); } catch { }
                try { await Task.WhenAny(old?.ExitTask ?? Task.CompletedTask, Task.Delay(5000, cancellationToken)); } catch { }
                try { old?.Stop(); } catch { }
            }
            catch { }

            // Start new ffmpeg streamer with RTMP and HLS
            try
            {
                var localStreamEnabled = config.GetValue<bool?>("Stream:Local:Enabled") ?? false;
                var hlsFolder = config.GetValue<string>("Stream:Local:HlsFolder") ?? Path.Combine(Directory.GetCurrentDirectory(), "hls");
                var targetFps = config.GetValue<int?>("Stream:TargetFps") ?? 6;
                var bitrateKbps = config.GetValue<int?>("Stream:BitrateKbps") ?? 800;

                // Overlay options
                FfmpegOverlayOptions? overlayOptions = null;
                if (config.GetValue<bool?>("Overlay:Enabled") ?? false)
                {
                    var overlayService = new OverlayTextService(config, null);
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
                if (string.IsNullOrWhiteSpace(source))
                {
                    yt.Dispose();
                    return (false, "Stream:Source is not configured");
                }
                var streamer = new FfmpegStreamer(source, newRtmp + "/" + newKey, targetFps, bitrateKbps, overlayOptions, localStreamEnabled ? hlsFolder : null);
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

                return (true, null);
            }
            catch (Exception ex)
            {
                yt.Dispose();
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
                                await StartYouTubeStreamAsync(config, streamCts.Token, enableTimelapse: false, timelapseProvider: null);
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
                            var uploadEnabled = config.GetValue<bool?>("Timelapse:Upload") ?? false;
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
                                    var uploadTimelapse = config.GetValue<bool?>("Timelapse:Upload") ?? false;
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
                                        Console.WriteLine("[Timelapse] Video upload to YouTube is disabled (Timelapse:Upload=false)");
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
                            var uploadTimelapse = config.GetValue<bool?>("Timelapse:Upload") ?? false;
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
                                Console.WriteLine("[Timelapse] Video upload to YouTube is disabled (Timelapse:Upload=false)");
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
        public static async Task StartYouTubeStreamAsync(IConfiguration config, CancellationToken cancellationToken, bool enableTimelapse = true, ITimelapseMetadataProvider? timelapseProvider = null)
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

            try
            {
                var oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
                var oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");
                bool useOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);

                // Read source and manual key from config now (Program.cs shouldn't pass them)
                var source = config.GetValue<string>("Stream:Source");
                var manualKey = config.GetValue<string>("YouTube:Key");

                // Respect new config flag: whether automatic LiveBroadcast creation is enabled
                var liveBroadcastEnabled = config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true;

                // Prepare overlay options early so native fallback can reuse them
                FfmpegOverlayOptions? overlayOptions = null;
                if (config.GetValue<bool?>("Overlay:Enabled") ?? false)
                {
                    try
                    {
                        overlayService = new OverlayTextService(config, timelapseProvider);
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

                if (useOAuth && liveBroadcastEnabled)
                {
                    Console.WriteLine("Using YouTube OAuth to create broadcast...");
                    ytService = new YouTubeControlService(config);

                    // Authenticate
                    if (!await ytService.AuthenticateAsync(cancellationToken))
                    {
                        Console.WriteLine("Failed to authenticate with YouTube. Exiting.");
                        return;
                    }

                    // Create broadcast and stream
                    var result = await ytService.CreateLiveBroadcastAsync(cancellationToken);
                    if (result.rtmpUrl == null || result.streamKey == null)
                    {
                        Console.WriteLine("Failed to create YouTube broadcast. Exiting.");
                        return;
                    }

                    rtmpUrl = result.rtmpUrl;
                    streamKey = result.streamKey;
                    broadcastId = result.broadcastId;
                    moonrakerFilename = result.filename;

                    Console.WriteLine($"YouTube broadcast created! Watch at: https://www.youtube.com/watch?v={broadcastId}");
                    // Dump the LiveBroadcast and LiveStream resources for debugging
                    try
                    {
                        await ytService.LogBroadcastAndStreamResourcesAsync(broadcastId, null, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to log broadcast/stream resources: {ex.Message}");
                    }

                    // Ensure and add broadcast to playlist if configured
                    try
                    {
                        var playlistName = config.GetValue<string>("YouTube:Playlist:Name");
                        if (!string.IsNullOrWhiteSpace(playlistName))
                        {
                            var playlistPrivacy = config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted";
                            var pid = await ytService.EnsurePlaylistAsync(playlistName, playlistPrivacy, cancellationToken);
                            if (!string.IsNullOrWhiteSpace(pid) && !string.IsNullOrWhiteSpace(broadcastId))
                            {
                                await ytService.AddVideoToPlaylistAsync(pid, broadcastId, cancellationToken);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[YouTube] Failed to add broadcast to playlist: {ex.Message}");
                    }

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
                else if (!string.IsNullOrWhiteSpace(manualKey))
                {
                    Console.WriteLine("Using manual YouTube stream key...");
                    rtmpUrl = "rtmp://a.rtmp.youtube.com/live2";
                    streamKey = manualKey;
                }
                else
                {
                    // If live broadcasts are disabled and no manual key is provided, proceed with HLS-only preview
                    if (!liveBroadcastEnabled)
                    {
                        Console.WriteLine("YouTube live broadcast creation disabled via config (YouTube:LiveBroadcast:Enabled=false). Starting local HLS preview only.");
                        rtmpUrl = null;
                        streamKey = null;
                    }
                    else
                    {
                        Console.WriteLine("Error: No YouTube credentials or stream key provided.");
                        return;
                    }
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

                // Always use the ffmpeg-based streamer. If fullRtmpUrl is null, ffmpeg will produce HLS-only preview.
                Console.WriteLine($"Starting ffmpeg streamer to {(fullRtmpUrl != null ? rtmpUrl + "/***" : "HLS-only")} (fps={targetFps}, kbps={bitrateKbps})");
                streamer = new FfmpegStreamer(source, fullRtmpUrl, targetFps, bitrateKbps, overlayOptions, localStreamEnabled ? hlsFolder : null);
                
                // Start streamer without awaiting so we can detect ingestion while it's running
                var streamerStartTask = streamer.StartAsync(cancellationToken);

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
                                var uploadTimelapse = config.GetValue<bool?>("Timelapse:Upload") ?? false;
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
                                    Console.WriteLine("[Timelapse] Video upload to YouTube is disabled (Timelapse:Upload=false)");
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
