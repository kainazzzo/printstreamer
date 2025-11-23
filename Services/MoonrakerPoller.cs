using PrintStreamer.Streamers;
using PrintStreamer.Timelapse;
using PrintStreamer.Overlay;
using PrintStreamer.Utils;
using PrintStreamer.Interfaces;
using PrintStreamer.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace PrintStreamer.Services
{
internal static class MoonrakerPoller
{
        private static MoonrakerClient? _moonrakerClient;
        /// <summary>
        /// Register a shared MoonrakerClient instance (from DI). The poller is static so it
        /// cannot use constructor injection; callers should set the client at startup.
        /// </summary>
        public static void SetMoonrakerClient(MoonrakerClient client)
        {
            _moonrakerClient = client;
        }

    // Shared runtime state for broadcast coordination. The actual encoder
    // (IStreamer) is owned and managed by the StreamOrchestrator; the poller
    // only keeps minimal broadcast identifiers and synchronization primitives.
    private static readonly object _streamLock = new object();
    // The poller no longer holds a long-lived YouTubeControlService instance;
    // control operations should be performed by consumers (or with short-lived
    // service instances) so the control service can also subscribe to events.
        private static string? _currentBroadcastId;
    // The poller must not depend on higher-level orchestrators. It only publishes
    // printer state events and exposes broadcast/stream helpers. Orchestrators
    // should subscribe to events or call these helpers as needed.
    // Monitor state exposed to the UI
    private static volatile bool _isWaitingForIngestion = false;
    // Semaphore to prevent concurrent broadcast creation
    private static readonly SemaphoreSlim _broadcastCreationLock = new SemaphoreSlim(1, 1);

    // Events fired when printer state changes (for PrintStreamOrchestrator to subscribe to)
    public static event PrintStateChangedEventHandler? PrintStateChanged;
    public static event PrintStartedEventHandler? PrintStarted;
    public static event PrintEndedEventHandler? PrintEnded;

    // Current printer state
    private static PrinterState? _currentPrinterState;
    public static PrinterState? CurrentPrinterState => _currentPrinterState;

    // Last completed job filename (nullable). Updated when a print finishes.
    private static string? _lastCompletedFilename;
    public static string? LastCompletedFilename => _lastCompletedFilename;

    // Camera simulation and blackout logic is handled only by WebCamManager.

        /// <summary>
        /// Cancel the current streamer (if any) and start a new one using the provided configuration.
        /// This is useful to force ffmpeg to re-read the configured source (for example when toggling
        /// camera simulation so the new streamer can use the local proxy endpoint).
        /// </summary>
        public static void RestartCurrentStreamerWithConfig(IConfiguration config, ILoggerFactory loggerFactory)
        {
            // The poller no longer owns the live encoder. Restart requests should be
            // handled by the StreamOrchestrator; log and return so callers can invoke
            // orchestrator behavior via DI/handlers.
            var logger = loggerFactory.CreateLogger(nameof(MoonrakerPoller));
            logger.LogInformation("Restart requested — delegate to StreamOrchestrator (poller no-op)");
        }

        public static bool IsBroadcastActive => !string.IsNullOrWhiteSpace(_currentBroadcastId);
        public static string? CurrentBroadcastId => _currentBroadcastId;
    public static bool IsWaitingForIngestion => _isWaitingForIngestion;

        /// <summary>
        /// Register a PrintStreamOrchestrator to handle printer state changes.
        /// </summary>
        public static void RegisterPrintStreamOrchestrator(PrintStreamOrchestrator orchestrator)
        {
            // The event handler is async, but the registrar itself does not need to be async.
            PrintStateChanged += async (prev, curr) => await orchestrator.HandlePrinterStateChangedAsync(prev, curr, CancellationToken.None);
        }

        // Note: Do NOT register orchestrators with the poller. The orchestrator
        // should subscribe to PrintStateChanged events and call Poller helpers when
        // it needs to control streaming or timelapse lifecycle.

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

            // The poller does not manage the live encoder; give orchestrator time to
            // react if necessary. Do not wait on a poller-owned streamer.

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

            // The poller does not start/stop encoder processes; the orchestrator
            // is responsible for promoting or restarting the encoder as needed.

            // Start new ffmpeg streamer with RTMP
            try
            {
                var localStreamEnabled = config.GetValue<bool?>("Stream:Local:Enabled") ?? false;
                var targetFps = config.GetValue<int?>("Stream:TargetFps") ?? 30;
                var bitrateKbps = config.GetValue<int?>("Stream:BitrateKbps") ?? 2500;

                // Determine audio source for ffmpeg when serving locally
                string? audioUrl = null;
                var useApiAudio = config.GetValue<bool?>("Stream:Audio:UseApiStream") ?? true;
                var audioFeatureEnabled = config.GetValue<bool?>("Audio:Enabled") ?? true;
                if ((config.GetValue<bool?>("Serve:Enabled") ?? true) && useApiAudio && audioFeatureEnabled)
                {
                    audioUrl = config.GetValue<string>("Stream:Audio:Url");
                    if (string.IsNullOrWhiteSpace(audioUrl)) audioUrl = "http://127.0.0.1:8080/api/audio/stream";
                }

                

                // Overlay options: only when not using the pre-composited overlay endpoint
                FfmpegOverlayOptions? overlayOptions = null;
                if (!(config.GetValue<bool?>("Serve:Enabled") ?? true) && (config.GetValue<bool?>("Overlay:Enabled") ?? false))
                {
                    var overlayService = new OverlayTextService(config, null, null, loggerFactory.CreateLogger<OverlayTextService>(), _moonrakerClient!);
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
                        X = config.GetValue<string>("Overlay:X") ?? "0",
                        Y = config.GetValue<string>("Overlay:Y") ?? "40"
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
                // Record the broadcast id so status endpoints reflect a live/broadcasting state.
                lock (_streamLock)
                {
                    _currentBroadcastId = newBroadcastId;
                }

                // The poller does not start encoder processes; the StreamOrchestrator
                // should start/attach the encoder to the RTMP endpoint returned above.
                yt.Dispose();
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
            // The poller no longer owns an in-process encoder. Only the broadcast id
            // is stored here so status endpoints reflect live state. The StreamOrchestrator
            // is responsible for stopping any running encoder; the poller will only
            // finalize the YouTube broadcast resources.
            string? broadcastId;
            lock (_streamLock)
            {
                broadcastId = _currentBroadcastId;
                _currentBroadcastId = null;
            }

            if (string.IsNullOrWhiteSpace(broadcastId))
            {
                return (false, "No active broadcast to stop");
            }

            try
            {
                // End the YouTube broadcast. Create a short-lived YouTubeControlService
                // to perform the EndBroadcast / playlist update operations so the
                // control service can be owned elsewhere (e.g., orchestrator) and
                // subscribe to PrintStateChanged events itself.
                if (!string.IsNullOrWhiteSpace(broadcastId))
                {
                    try
                    {
                        var ytLocal = new YouTubeControlService(config, loggerFactory.CreateLogger<YouTubeControlService>());
                        if (await ytLocal.AuthenticateAsync(cancellationToken))
                        {
                            try
                            {
                                await ytLocal.EndBroadcastAsync(broadcastId, cancellationToken);
                                logger.LogInformation("[YouTube] Ended broadcast {BroadcastId}", broadcastId);

                                // Add the completed broadcast to the playlist
                                try
                                {
                                    await Task.Delay(2000, cancellationToken);
                                    var playlistName = config.GetValue<string>("YouTube:Playlist:Name");
                                    if (!string.IsNullOrWhiteSpace(playlistName))
                                    {
                                        logger.LogInformation("[YouTube] Adding completed broadcast to playlist '{PlaylistName}'...", playlistName);
                                        var playlistPrivacy = config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted";
                                        var pid = await ytLocal.EnsurePlaylistAsync(playlistName, playlistPrivacy, cancellationToken);
                                        if (!string.IsNullOrWhiteSpace(pid))
                                        {
                                            var added = await ytLocal.AddVideoToPlaylistAsync(pid, broadcastId, cancellationToken);
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
                        }
                        ytLocal.Dispose();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[StopBroadcast] Error ending broadcast (yt)");
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
            // Poller does not start local streamers or hold YouTube control clients;
            // orchestrator owns encoder lifecycle and upload clients.
            var offlineGrace = config.GetValue<TimeSpan?>("Timelapse:OfflineGracePeriod") ?? TimeSpan.FromMinutes(10);
            var idleFinalizeDelay = config.GetValue<TimeSpan?>("Timelapse:IdleFinalizeDelay") ?? TimeSpan.FromSeconds(20);
            var activeStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "printing",
                "paused",
                "pausing",
                "resuming",
                "resumed",
                "cancelling",
                "finishing",
                "heating",
                "preheating",
                "cooling"
            };
            var doneStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "standby",
                "idle",
                "ready",
                "complete",
                "completed",
                "success",
                "cancelled",
                "canceled",
                "error"
            };
            DateTime lastInfoSeenAt = DateTime.UtcNow;
            DateTime? lastPrintingSeenAt = null;
            DateTime? idleStateSince = null;
            DateTime? jobMissingSince = null;
            // (timelapse session specific variables removed - orchestrator owns timelapse lifecycle)
            bool waitingForResumeLogged = false;

            try
            {
                // Use DI-provided TimelapseManager for G-code caching and frame capture (injected by caller)
                // timelapseManager is passed in as a parameter
                
                // Orchestrator is responsible for YouTube authentication and uploads.

                while (!cancellationToken.IsCancellationRequested)
                {
                    TimeSpan pollInterval = basePollInterval; // Default poll interval
                    try
                    {
                    // Query Moonraker job queue
                    var baseUri = new Uri(moonrakerBase);
                    var info = _moonrakerClient != null ? await _moonrakerClient.GetPrintInfoAsync(baseUri, apiKey, authHeader, cancellationToken) : null;
                    var infoAvailable = info != null;
                    if (infoAvailable)
                    {
                        lastInfoSeenAt = DateTime.UtcNow;
                        if (waitingForResumeLogged)
                        {
                            waitingForResumeLogged = false;
                        }
                    }

                    var currentJob = info?.Filename;
                    var jobQueueId = info?.JobQueueId;
                    var state = info?.State;
                    var isPrinting = string.Equals(state, "printing", StringComparison.OrdinalIgnoreCase);
                    var remaining = info?.Remaining;
                    var progressPct = info?.ProgressPercent;
                    var currentLayer = info?.CurrentLayer;
                    var totalLayers = info?.TotalLayers;

                    watcherLogger.LogDebug("[Watcher] Poll result - Filename: '{Filename}', State: '{State}', Progress: {Progress}%, Remaining: {Remaining}, Layer: {Layer}/{Total}", currentJob, state, progressPct?.ToString("F1") ?? "n/a", remaining?.ToString() ?? "n/a", currentLayer?.ToString() ?? "n/a", totalLayers?.ToString() ?? "n/a");

                    // Fire PrinterState events for subscribers (e.g., PrintStreamOrchestrator)
                    UpdateAndFirePrinterStateEvents(watcherLogger, state, currentJob, jobQueueId, progressPct, remaining, currentLayer, totalLayers);

                    if (isPrinting || (!string.IsNullOrWhiteSpace(state) && activeStates.Contains(state)))
                    {
                        lastPrintingSeenAt = DateTime.UtcNow;
                        idleStateSince = null;
                    }
                    else if (!string.IsNullOrWhiteSpace(state) && doneStates.Contains(state))
                    {
                        idleStateSince ??= DateTime.UtcNow;
                    }

                    // Track job missing state based on presence of a current job
                    if (string.IsNullOrWhiteSpace(currentJob))
                    {
                        jobMissingSince ??= DateTime.UtcNow;
                    }
                    else
                    {
                        jobMissingSince = null;
                    }

                    bool forceFinalizeActiveSession = false;
                    // If a printing job filename changes while printing, trigger finalize for previous job state
                    if (isPrinting && !string.IsNullOrWhiteSpace(currentJob) && !string.IsNullOrWhiteSpace(lastJobFilename) &&
                        !string.Equals(currentJob, lastJobFilename, StringComparison.OrdinalIgnoreCase))
                    {
                        forceFinalizeActiveSession = true;
                        watcherLogger.LogInformation("[Watcher] Detected job change while printing. Previous job: {PreviousJob}, new job: {CurrentJob}. Preparing to finalize previous job.", lastJobFilename, currentJob);
                    }

                    if (isPrinting)
                    {
                        waitingForResumeLogged = false;
                    }

                    // No direct timelapse manager actions here; orchestration of timelapse lifecycle
                    // is the responsibility of PrintStreamOrchestrator which subscribes to PrinterState events.

                    // When a new print job starts, publish the event (above) and let
                    // the orchestrator decide whether to start/attach streaming/timelapse.
                    if (!forceFinalizeActiveSession && isPrinting &&
                        (string.IsNullOrWhiteSpace(currentJob) || !string.Equals(currentJob, lastJobFilename, StringComparison.OrdinalIgnoreCase)))
                    {
                        var fallbackJobName = $"__printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                        var jobLabel = !string.IsNullOrWhiteSpace(currentJob)
                            ? currentJob
                            : (!string.IsNullOrWhiteSpace(lastJobFilename) ? lastJobFilename! : fallbackJobName);
                        watcherLogger.LogInformation("[Watcher] New print job detected: {Job}", currentJob ?? "(unknown)");
                        if (!string.IsNullOrWhiteSpace(currentJob)) lastJobFilename = currentJob;
                        jobMissingSince = null;
                        idleStateSince = null;
                        lastPrintingSeenAt = DateTime.UtcNow;
                    }
                    // Timelapse last-layer detection and early finalize handled by orchestrator.
                    else if ((forceFinalizeActiveSession || !isPrinting))
                    {
                        var now = DateTime.UtcNow;
                        var layerOffset = config.GetValue<int?>("Timelapse:LastLayerOffset") ?? 1;
                        if (layerOffset < 0) layerOffset = 0;

                        bool layersComplete = currentLayer.HasValue && totalLayers.HasValue && totalLayers.Value > 0 &&
                                              currentLayer.Value >= Math.Max(0, totalLayers.Value - layerOffset);
                        bool progressComplete = progressPct.HasValue && progressPct.Value >= 99.0;
                        bool idleMet = idleStateSince.HasValue && (now - idleStateSince.Value) >= idleFinalizeDelay;
                        bool jobMissingMet = jobMissingSince.HasValue && (now - jobMissingSince.Value) >= offlineGrace;
                        bool offlineMet = lastPrintingSeenAt.HasValue && (now - lastPrintingSeenAt.Value) >= offlineGrace && (now - lastInfoSeenAt) >= offlineGrace;

                        var shouldFinalize = forceFinalizeActiveSession || layersComplete || progressComplete || idleMet || jobMissingMet || offlineMet;

                        if (!shouldFinalize)
                        {
                            if (!waitingForResumeLogged)
                            {
                                watcherLogger.LogDebug("[Watcher] Holding timelapse open (state={State}, progress={Progress}%, idleFor={Idle}, jobMissingFor={JobMissing}, offlineFor={Offline}).",
                                    state ?? "n/a",
                                    progressPct?.ToString("F1") ?? "n/a",
                                    idleStateSince.HasValue ? FormatTimeSpan(now - idleStateSince.Value) : "n/a",
                                    jobMissingSince.HasValue ? FormatTimeSpan(now - jobMissingSince.Value) : "n/a",
                                    lastPrintingSeenAt.HasValue ? FormatTimeSpan(now - lastPrintingSeenAt.Value) : "n/a");
                                waitingForResumeLogged = true;
                            }
                        }
                        else
                        {
                            waitingForResumeLogged = false;
                            // Job finished — orchestrator will finalize timelapse and stop streaming.
                            var jobLogName = lastJobFilename ?? "(unknown)";
                            if (forceFinalizeActiveSession)
                            {
                                watcherLogger.LogInformation("[Watcher] Finalizing active timelapse session before starting new job: {Job}", jobLogName);
                            }
                            else
                            {
                                watcherLogger.LogInformation("[Watcher] Print job finished: {Job}", jobLogName);
                            }
                            var finishedJobFilename = lastJobFilename;
                            if (!forceFinalizeActiveSession)
                            {
                                lastCompletedJobFilename = finishedJobFilename;
                            }
                            // Store last completed job filename in a static accessible property
                            if (!string.IsNullOrWhiteSpace(finishedJobFilename)) _lastCompletedFilename = finishedJobFilename;
                            lastJobFilename = null;

                            jobMissingSince = null;
                            idleStateSince = null;
                            lastPrintingSeenAt = null;
                        }
                    }
                    else
                    {
                        waitingForResumeLogged = false;
                    }

                    // Adaptive polling: poll faster when we're near completion (must be inside try block)
                    pollInterval = basePollInterval;
                    if (isPrinting)
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
                // Poller cleanup complete. Orchestrator owns encoder and upload lifecycle.
                watcherLogger.LogInformation("[Watcher] Cleanup complete.");
            }
        }

        // Stream helper moved from Program.cs
    public static async Task StartYouTubeStreamAsync(IConfiguration config, ILoggerFactory loggerFactory, CancellationToken cancellationToken, bool enableTimelapse = true, ITimelapseMetadataProvider? timelapseProvider = null, bool allowYouTube = true)
        {
            var streamLogger = loggerFactory.CreateLogger(nameof(MoonrakerPoller));
            // Poller runs the internal streaming implementation below. Orchestrators
            // should call these helpers when they need to control broadcasts/streams.
            string? rtmpUrl = null;
            string? streamKey = null;
            string? broadcastId = null;
            string? moonrakerFilename = null;
            YouTubeControlService? ytService = null;
            TimelapseService? timelapse = null;
            CancellationTokenSource? timelapseCts = null;
            Task? timelapseTask = null;
            OverlayTextService? overlayService = null;

            // The poller does not own or start encoder processes. If an encoder
            // needs restarting, the StreamOrchestrator should handle it. No action
            // is taken here regarding in-process streamers.
            try
            {

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
                        overlayService = new OverlayTextService(config, timelapseProvider, null, loggerFactory.CreateLogger<OverlayTextService>(), _moonrakerClient!);
                        overlayService.Start();
                        overlayOptions = new FfmpegOverlayOptions
                        {
                            TextFile = overlayService.TextFilePath,
                            FontFile = config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
                            FontSize = config.GetValue<int?>("Overlay:FontSize") ?? 22,
                            FontColor = config.GetValue<string>("Overlay:FontColor") ?? "white",
                            Box = config.GetValue<bool?>("Overlay:Box") ?? true,
                            BoxColor = config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4",
                            BoxBorderW = config.GetValue<int?>("Overlay:BoxBorderW") ?? 2,
                            X = config.GetValue<string>("Overlay:X") ?? "0",
                            Y = config.GetValue<string>("Overlay:Y") ?? "40"
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
                var audioFeatureEnabled = config.GetValue<bool?>("Audio:Enabled") ?? true;
                if ((config.GetValue<bool?>("Serve:Enabled") ?? true) && useApiAudio && audioFeatureEnabled)
                {
                    audioUrl = config.GetValue<string>("Stream:Audio:Url");
                    if (string.IsNullOrWhiteSpace(audioUrl)) audioUrl = "http://127.0.0.1:8080/api/audio/stream";
                }

                // The poller does not create or start the ffmpeg process. It returns
                // broadcast information and records the broadcast id so that the
                // orchestrator or other consumers can create/attach an encoder as
                // needed (for example using the returned RTMP endpoint).
                streamLogger.LogInformation("Stream ready: orchestrator should start encoder for {Target} (fps={Fps}, kbps={Kbps})", fullRtmpUrl != null ? rtmpUrl + "/***" : "local preview", targetFps, bitrateKbps);

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

                // Wait until cancellation is requested; the orchestrator is expected
                // to cancel this operation when the stream ends. This preserves the
                // timelapse lifecycle and ensures finalization runs below.
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException) { /* expected on shutdown */ }
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
                // Encoder/process stop is handled by the orchestrator if needed.

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
                            // _currentYouTubeService removed; nothing to clear here
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

        private static string FormatTimeSpan(TimeSpan ts)
        {
            try
            {
                // Use a safe TimeSpan format and fall back to default if something unexpected occurs
                return ts.ToString(@"hh\:mm\:ss");
            }
            catch
            {
                return ts.ToString();
            }
        }

        /// <summary>
        /// Create a PrinterState snapshot from Moonraker poll data and fire events if state changed.
        /// </summary>
        private static void UpdateAndFirePrinterStateEvents(
            ILogger logger,
            string? state,
            string? filename,
            string? jobQueueId,
            double? progressPercent,
            TimeSpan? remaining,
            int? currentLayer,
            int? totalLayers)
        {
            var newState = new PrinterState
            {
                State = state,
                Filename = filename,
                JobQueueId = jobQueueId,
                ProgressPercent = progressPercent,
                Remaining = remaining,
                CurrentLayer = currentLayer,
                TotalLayers = totalLayers,
                SnapshotTime = DateTime.UtcNow
            };

            var previousState = _currentPrinterState;
            _currentPrinterState = newState;

            // Fire generic "state changed" event
            PrintStateChanged?.Invoke(previousState, newState);

            // Fire specific events for print start/end transitions
            bool wasPrinting = previousState?.IsActivelyPrinting ?? false;
            bool isPrintingNow = newState.IsActivelyPrinting;

            if (!wasPrinting && isPrintingNow)
            {
                PrintStarted?.Invoke(newState);
            }
            else if (wasPrinting && !isPrintingNow)
            {
                PrintEnded?.Invoke(newState);
            }
        }
    }
}
