using PrintStreamer.Streamers;
using PrintStreamer.Timelapse;
using PrintStreamer.Overlay;
using PrintStreamer.Utils;
using PrintStreamer.Interfaces;
using PrintStreamer.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Services
{
    internal class MoonrakerPoller : IMoonrakerPoller
    {
        public MoonrakerPoller(MoonrakerClient moonrakerClient, YouTubeControlService youTubeControlService, IConfiguration configuration, ILogger<MoonrakerPoller> logger)
        {
            _moonrakerClient = moonrakerClient;
            _youTubeControlService = youTubeControlService;
            _configuration = configuration;
            _logger = logger;
        }

        private MoonrakerClient? _moonrakerClient;

        // Compatibility helpers for older callers that expect MoonrakerPoller to provide
        // basic YouTube broadcast helpers. These are thin shims that delegate to the
        // DI-provided YouTubeControlService when available.
        private YouTubeControlService _youTubeControlService;

        private readonly IConfiguration _configuration;
        private readonly ILogger<MoonrakerPoller> _logger;


        /// <summary>
        /// Simple compatibility flag. The real broadcast state is managed elsewhere.
        /// </summary>
        public static bool IsBroadcastActive => false;

        /// <summary>
        /// Create a live broadcast using YouTubeControlService if registered.
        /// Returns (success, message, broadcastId).
        /// </summary>
        public async Task<(bool success, string? message, string? broadcastId)> StartBroadcastAsync(CancellationToken cancellationToken)
        {
            if (_youTubeControlService == null)
            {
                _logger.LogWarning("StartBroadcastAsync: YouTubeControlService not registered");
                return (false, "YouTube service unavailable", null);
            }

            try
            {
                if (!await _youTubeControlService.AuthenticateAsync(cancellationToken))
                {
                    return (false, "YouTube authentication failed", null);
                }

                var (rtmpUrl, streamKey, broadcastId, filename) = await _youTubeControlService.CreateLiveBroadcastAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(broadcastId))
                {
                    return (true, null, broadcastId);
                }
                return (false, "Failed to create broadcast", null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StartBroadcastAsync failed");
                return (false, ex.Message, null);
            }
        }

        /// <summary>
        /// Stop a broadcast. Compatibility shim that currently returns not-implemented.
        /// </summary>
        public static Task<(bool ok, string? message)> StopBroadcastAsync(IConfiguration config, CancellationToken cancellationToken, ILogger logger)
        {
            logger.LogWarning("StopBroadcastAsync: Not implemented in MoonrakerPoller compatibility shim");
            return Task.FromResult((false, (string?)"Not implemented"));
        }

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

        /// <summary>
        /// Cancel the current streamer (if any) and start a new one using the provided configuration.
        /// This is kept as a no-op: the poller does not own encoders or orchestrate ffmpeg.
        /// </summary>
        public static void RestartCurrentStreamerWithConfig(IConfiguration config, ILogger logger)
        {
            logger.LogInformation("Restart requested — delegate to StreamOrchestrator (poller no-op)");
        }

        /// <summary>
        /// Register a PrintStreamOrchestrator to handle printer state changes.
        /// </summary>
        public void RegisterPrintStreamOrchestrator(PrintStreamOrchestrator orchestrator)
        {
          
            // The event handler is async, but the registrar itself does not need to be async.
            PrintStateChanged += async (prev, curr) => await orchestrator.HandlePrinterStateChangedAsync(prev, curr, CancellationToken.None);
        
        }

        // Polling loop: query Moonraker and emit PrinterState events for subscribers.
        public async Task PollAndStreamJobsAsync(CancellationToken cancellationToken)
        {
            var moonrakerBase = _configuration.GetValue<string>("Moonraker:BaseUrl") ?? "http://localhost:7125/";
            var apiKey = _configuration.GetValue<string>("Moonraker:ApiKey");
            var authHeader = _configuration.GetValue<string>("Moonraker:AuthHeader");
            var basePollInterval = TimeSpan.FromSeconds(10); // configurable if desired
            var fastPollInterval = TimeSpan.FromSeconds(2); // faster polling near completion
            string? lastJobFilename = null;
            string? lastCompletedJobFilename = null; // used for final upload/title if app shuts down post-completion
            var offlineGrace = _configuration.GetValue<TimeSpan?>("Timelapse:OfflineGracePeriod") ?? TimeSpan.FromMinutes(10);
            var idleFinalizeDelay = _configuration.GetValue<TimeSpan?>("Timelapse:IdleFinalizeDelay") ?? TimeSpan.FromSeconds(20);
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
            bool waitingForResumeLogged = false;

            try
            {
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

                        _logger.LogDebug("[Watcher] Poll result - Filename: '{Filename}', State: '{State}', Progress: {Progress}%, Remaining: {Remaining}, Layer: {Layer}/{Total}",
                            currentJob, state, progressPct?.ToString("F1") ?? "n/a", remaining?.ToString() ?? "n/a",
                            currentLayer?.ToString() ?? "n/a", totalLayers?.ToString() ?? "n/a");

                        // Fire PrinterState events for subscribers (e.g., PrintStreamOrchestrator)
                        UpdateAndFirePrinterStateEvents(state, currentJob, jobQueueId, progressPct, remaining, currentLayer, totalLayers);

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
                            _logger.LogInformation("[Watcher] Detected job change while printing. Previous job: {PreviousJob}, new job: {CurrentJob}. Preparing to finalize previous job.", lastJobFilename, currentJob);
                        }

                        if (isPrinting)
                        {
                            waitingForResumeLogged = false;
                        }

                        // When a new print job starts, publish the event (above) and let
                        // the orchestrator decide whether to start/attach streaming/timelapse.
                        if (!forceFinalizeActiveSession && isPrinting &&
                            (string.IsNullOrWhiteSpace(currentJob) || !string.Equals(currentJob, lastJobFilename, StringComparison.OrdinalIgnoreCase)))
                        {
                            var fallbackJobName = $"__printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                            var jobLabel = !string.IsNullOrWhiteSpace(currentJob)
                                ? currentJob
                                : (!string.IsNullOrWhiteSpace(lastJobFilename) ? lastJobFilename! : fallbackJobName);
                            _logger.LogInformation("[Watcher] New print job detected: {Job}", currentJob ?? "(unknown)");
                            if (!string.IsNullOrWhiteSpace(currentJob)) lastJobFilename = currentJob;
                            jobMissingSince = null;
                            idleStateSince = null;
                            lastPrintingSeenAt = DateTime.UtcNow;
                        }
                        // Job finalization decisions are left to orchestrator.
                        else if ((forceFinalizeActiveSession || !isPrinting))
                        {
                            var now = DateTime.UtcNow;
                            var layerOffset = _configuration.GetValue<int?>("Timelapse:LastLayerOffset") ?? 1;
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
                                    _logger.LogDebug("[Watcher] Holding timelapse open (state={State}, progress={Progress}%, idleFor={Idle}, jobMissingFor={JobMissing}, offlineFor={Offline}).",
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
                                    _logger.LogInformation("[Watcher] Finalizing active timelapse session before starting new job: {Job}", jobLogName);
                                }
                                else
                                {
                                    _logger.LogInformation("[Watcher] Print job finished: {Job}", jobLogName);
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
                                _logger.LogDebug("[Watcher] Using fast polling ({Seconds}s) - near completion", pollInterval.TotalSeconds);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Watcher] Error");
                    }

                    await Task.Delay(pollInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Watcher] Polling cancelled by user.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Watcher] Unexpected error");
            }
            finally
            {
                _logger.LogInformation("[Watcher] Shutting down...");
                _logger.LogInformation("[Watcher] Cleanup complete.");
            }
        }

        private static string FormatTimeSpan(TimeSpan ts)
        {
            try
            {
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
        private void UpdateAndFirePrinterStateEvents(
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
