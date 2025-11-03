using PrintStreamer.Timelapse;

namespace PrintStreamer.Services
{
    /// <summary>
    /// Polls Moonraker for print job status and coordinates with StreamOrchestrator
    /// to start/stop broadcasts and timelapses automatically.
    /// </summary>
    public class MoonrakerPollerService
    {
        private readonly IConfiguration _config;
        private readonly StreamOrchestrator _orchestrator;
        private readonly TimelapseManager _timelapseManager;
        private readonly bool _verbosePollerLogs;

        public MoonrakerPollerService(
            IConfiguration config,
            StreamOrchestrator orchestrator,
            TimelapseManager timelapseManager)
        {
            _config = config;
            _orchestrator = orchestrator;
            _timelapseManager = timelapseManager;
            _verbosePollerLogs = _config.GetValue<bool?>("Moonraker:VerboseLogs") ?? false;
        }

        /// <summary>
        /// Start polling Moonraker and automatically manage broadcasts/timelapses
        /// </summary>
        public async Task StartPollingAsync(CancellationToken cancellationToken)
        {
            var moonrakerBase = _config.GetValue<string>("Moonraker:BaseUrl") ?? "http://localhost:7125/";
            var apiKey = _config.GetValue<string>("Moonraker:ApiKey");
            var authHeader = _config.GetValue<string>("Moonraker:AuthHeader");
            var basePollInterval = TimeSpan.FromSeconds(10);
            var fastPollInterval = TimeSpan.FromSeconds(2);
            
            string? lastJobFilename = null;
            string? lastState = null; // Track previous state to detect transitions
            string? activeTimelapseSession = null;
            bool lastLayerTriggered = false;

            Console.WriteLine("[MoonrakerPoller] Starting polling loop");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TimeSpan pollInterval = basePollInterval;

                    try
                    {
                        var baseUri = new Uri(moonrakerBase);
                        var info = await MoonrakerClient.GetPrintInfoAsync(baseUri, apiKey, authHeader, cancellationToken);
                        
                        var currentJob = info?.Filename;
                        var state = info?.State;
                        var isPrinting = string.Equals(state, "printing", StringComparison.OrdinalIgnoreCase);
                        var remaining = info?.Remaining;
                        var progressPct = info?.ProgressPercent;
                        var currentLayer = info?.CurrentLayer;
                        var totalLayers = info?.TotalLayers;

                        // Suppress noisy per-iteration state logging unless explicitly enabled
                        if (_verbosePollerLogs)
                        {
                            Console.WriteLine($"[MoonrakerPoller] State: {state}, Job: {currentJob}, Progress: {progressPct?.ToString("F1") ?? "n/a"}%");
                        }

                        // Detect state transition from printing to not-printing (complete/standby/etc)
                        bool printJustFinished = lastState == "printing" && state != "printing";
                        
                        // Update last state for next iteration
                        lastState = state;

                        // Update timelapse progress
                        if (activeTimelapseSession != null)
                        {
                            try
                            {
                                _timelapseManager.NotifyPrintProgress(activeTimelapseSession, currentLayer, totalLayers);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[MoonrakerPoller] Failed to notify timelapse: {ex.Message}");
                            }
                        }

                        // Start broadcast when printing begins
                        if (isPrinting && !_orchestrator.IsBroadcastActive && (string.IsNullOrWhiteSpace(currentJob) || currentJob != lastJobFilename))
                        {
                            Console.WriteLine($"[MoonrakerPoller] New print detected: {currentJob}");
                            lastJobFilename = currentJob ?? $"__printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                            
                            // Start broadcast if auto-broadcast is enabled
                            var autoBroadcastEnabled = _config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true;
                            if (autoBroadcastEnabled)
                            {
                                try
                                {
                                    var (success, message, broadcastId) = await _orchestrator.StartBroadcastAsync(cancellationToken);
                                    if (success)
                                    {
                                        Console.WriteLine($"[MoonrakerPoller] Broadcast started: {broadcastId}");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[MoonrakerPoller] Failed to start broadcast: {message}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[MoonrakerPoller] Error starting broadcast: {ex.Message}");
                                }
                            }
                            else
                            {
                                // Just start local stream
                                try
                                {
                                    await _orchestrator.StartLocalStreamAsync(cancellationToken);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[MoonrakerPoller] Error starting local stream: {ex.Message}");
                                }
                            }

                            // Start timelapse
                            var jobNameSafe = !string.IsNullOrWhiteSpace(currentJob) 
                                ? SanitizeFilename(currentJob) 
                                : $"printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                            
                            activeTimelapseSession = await _timelapseManager.StartTimelapseAsync(jobNameSafe, currentJob);
                            lastLayerTriggered = false;
                        }
                        // Detect last layer and finalize timelapse early
                        else if (isPrinting && activeTimelapseSession != null && !lastLayerTriggered)
                        {
                            var thresholdSecs = _config.GetValue<int?>("Timelapse:LastLayerRemainingSeconds") ?? 30;
                            var thresholdPct = _config.GetValue<double?>("Timelapse:LastLayerProgressPercent") ?? 98.5;
                            var layerThreshold = _config.GetValue<int?>("Timelapse:LastLayerOffset") ?? 1;

                            bool lastLayerByTime = remaining.HasValue && remaining.Value <= TimeSpan.FromSeconds(thresholdSecs);
                            bool lastLayerByProgress = progressPct.HasValue && progressPct.Value >= thresholdPct;
                            bool lastLayerByLayer = currentLayer.HasValue && totalLayers.HasValue &&
                                                    totalLayers.Value > 0 &&
                                                    currentLayer.Value >= (totalLayers.Value - layerThreshold);

                            if (lastLayerByTime || lastLayerByProgress || lastLayerByLayer)
                            {
                                Console.WriteLine($"[MoonrakerPoller] Last layer detected, finalizing timelapse");
                                lastLayerTriggered = true;

                                var sessionToFinalize = activeTimelapseSession;
                                activeTimelapseSession = null;

                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var videoPath = await _timelapseManager.StopTimelapseAsync(sessionToFinalize!);
                                        
                                        // Upload if enabled
                                        var uploadEnabled = _config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false;
                                        if (uploadEnabled && !string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
                                        {
                                            // TODO: Upload timelapse via YouTubeControlService
                                            Console.WriteLine($"[MoonrakerPoller] Timelapse video ready: {videoPath}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[MoonrakerPoller] Error finalizing timelapse: {ex.Message}");
                                    }
                                }, CancellationToken.None);
                            }
                        }
                        // Stop broadcast when print finishes (state transition from printing to complete/standby)
                        // Only if we have a lastJobFilename (meaning poller was tracking a print job)
                        else if (printJustFinished && _orchestrator.IsBroadcastActive && lastJobFilename != null)
                        {
                            Console.WriteLine($"[MoonrakerPoller] Print finished (state: {lastState} -> {state}), stopping broadcast");
                            lastJobFilename = null;

                            try
                            {
                                var (success, message) = await _orchestrator.StopBroadcastAsync(CancellationToken.None);
                                if (success)
                                {
                                    Console.WriteLine("[MoonrakerPoller] Broadcast stopped");
                                }
                                else
                                {
                                    Console.WriteLine($"[MoonrakerPoller] Error stopping broadcast: {message}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[MoonrakerPoller] Exception stopping broadcast: {ex.Message}");
                            }

                            // Finalize timelapse if still active
                            if (activeTimelapseSession != null)
                            {
                                try
                                {
                                    await _timelapseManager.StopTimelapseAsync(activeTimelapseSession);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[MoonrakerPoller] Error stopping timelapse: {ex.Message}");
                                }
                                activeTimelapseSession = null;
                            }
                        }

                        // Adaptive polling near completion
                        if (isPrinting && !lastLayerTriggered)
                        {
                            bool nearCompletion = (remaining.HasValue && remaining.Value <= TimeSpan.FromMinutes(2)) ||
                                                  (progressPct.HasValue && progressPct.Value >= 95.0) ||
                                                  (currentLayer.HasValue && totalLayers.HasValue && totalLayers.Value > 0 &&
                                                   currentLayer.Value >= (totalLayers.Value - 5));
                            if (nearCompletion)
                            {
                                pollInterval = fastPollInterval;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MoonrakerPoller] Error: {ex.Message}");
                    }

                    await Task.Delay(pollInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[MoonrakerPoller] Polling cancelled");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MoonrakerPoller] Unexpected error: {ex.Message}");
            }
        }

        private static string SanitizeFilename(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                return "unknown";

            var nameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
            var invalidChars = Path.GetInvalidFileNameChars()
                .Concat(Path.GetInvalidPathChars())
                .Concat(new[] { ' ', '-', '(', ')', '[', ']', '{', '}', ':', ';', ',', '.', '#' })
                .Distinct()
                .ToArray();

            var result = nameWithoutExtension;
            foreach (var c in invalidChars)
            {
                result = result.Replace(c, '_');
            }

            result = result.Replace("&", "and");
            while (result.Contains("__"))
            {
                result = result.Replace("__", "_");
            }

            result = result.Trim('_');
            return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
        }
    }
}
