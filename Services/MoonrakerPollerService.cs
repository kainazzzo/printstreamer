using PrintStreamer.Timelapse;
using Microsoft.Extensions.Logging;

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
        private readonly ILogger<MoonrakerPollerService> _logger;
        private readonly MoonrakerClient _moonrakerClient;
        private readonly bool _verbosePollerLogs;

        public MoonrakerPollerService(
            IConfiguration config,
            StreamOrchestrator orchestrator,
            TimelapseManager timelapseManager,
            ILogger<MoonrakerPollerService> logger,
            MoonrakerClient moonrakerClient)
        {
            _config = config;
            _orchestrator = orchestrator;
            _timelapseManager = timelapseManager;
            _logger = logger;
            _moonrakerClient = moonrakerClient;
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

            _logger.LogInformation("[MoonrakerPoller] Starting polling loop");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TimeSpan pollInterval = basePollInterval;

                    try
                    {
                        var baseUri = new Uri(moonrakerBase);
                        var info = await _moonrakerClient.GetPrintInfoAsync(baseUri, apiKey, authHeader, cancellationToken);
                        
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
                            _logger.LogDebug("[MoonrakerPoller] State: {State}, Job: {Job}, Progress: {Progress}%", state, currentJob, progressPct?.ToString("F1") ?? "n/a");
                        }

                        // Detect state transitions
                        bool printJustFinished = lastState == "printing" && state != "printing";
                        bool printJustStarted = lastState != "printing" && isPrinting;
                        
                        // Update last state for next iteration
                        lastState = state;

                        // Update timelapse progress and check if it auto-finalized
                        if (activeTimelapseSession != null && !lastLayerTriggered)
                        {
                            try
                            {
                                var videoPath = await _timelapseManager.NotifyPrintProgressAsync(activeTimelapseSession, currentLayer, totalLayers);
                                if (!string.IsNullOrWhiteSpace(videoPath))
                                {
                                    // Timelapse auto-finalized via NotifyPrintProgressAsync
                                    _logger.LogInformation("[MoonrakerPoller] Timelapse auto-finalized: {VideoPath}", videoPath);
                                    activeTimelapseSession = null;
                                    lastLayerTriggered = true;
                                    
                                    // Upload if enabled
                                    var uploadEnabled = _config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false;
                                    if (uploadEnabled && File.Exists(videoPath))
                                    {
                                        // TODO: Upload timelapse via YouTubeControlService
                                        _logger.LogInformation("[MoonrakerPoller] Timelapse video ready for upload: {VideoPath}", videoPath);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[MoonrakerPoller] Failed to notify timelapse");
                            }
                        }

                        // Start broadcast (or local stream) when a print begins (state transition)
                        if (printJustStarted && !_orchestrator.IsBroadcastActive)
                        {
                            _logger.LogInformation("[MoonrakerPoller] Print started: {Job}", currentJob);
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
                                        _logger.LogInformation("[MoonrakerPoller] Broadcast started: {BroadcastId}", broadcastId);
                                    }
                                    else
                                    {
                                        _logger.LogWarning("[MoonrakerPoller] Failed to start broadcast: {Message}", message);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "[MoonrakerPoller] Error starting broadcast");
                                }
                            }
                            else
                            {
                                // Manual mode: ensure a local stream is running continuously; avoid restarting if already streaming
                                if (!_orchestrator.IsStreaming)
                                {
                                        try
                                        {
                                            _logger.LogInformation("[MoonrakerPoller] Manual mode: starting local stream (no auto-broadcast)");
                                            await _orchestrator.StartLocalStreamAsync(cancellationToken);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "[MoonrakerPoller] Error starting local stream");
                                        }
                                }
                            }

                            // Start timelapse
                            var jobNameSafe = !string.IsNullOrWhiteSpace(currentJob) 
                                ? SanitizeFilename(currentJob) 
                                : $"printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                            
                            activeTimelapseSession = await _timelapseManager.StartTimelapseAsync(jobNameSafe, currentJob);
                            lastLayerTriggered = false;
                        }
                        // Stop/cleanup when print finishes (state transition from printing to complete/standby)
                        // If we have a lastJobFilename, we were tracking a print and must reset regardless of broadcast state
                        else if (printJustFinished && lastJobFilename != null)
                        {
                            _logger.LogInformation("[MoonrakerPoller] Print finished (state: printing -> {State}), cleaning up", state ?? "unknown");
                            lastJobFilename = null;

                            // Optionally stop the broadcast if one is active and the end-after-print feature is enabled
                            if (_orchestrator.IsBroadcastActive)
                            {
                                var endStreamAfterPrint = _config.GetValue<bool?>("YouTube:LiveBroadcast:EndStreamAfterPrint") ?? true;
                                if (endStreamAfterPrint)
                                {
                                    try
                                    {
                                        var (success, message) = await _orchestrator.StopBroadcastAsync(CancellationToken.None);
                                        if (success)
                                        {
                                            _logger.LogInformation("[MoonrakerPoller] Broadcast stopped (end stream after print enabled)");
                                        }
                                        else
                                        {
                                            _logger.LogWarning("[MoonrakerPoller] Error stopping broadcast: {Message}", message);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "[MoonrakerPoller] Exception stopping broadcast");
                                    }
                                }
                                else
                                {
                                    _logger.LogInformation("[MoonrakerPoller] End stream after print disabled; leaving broadcast running.");
                                }
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
                                    _logger.LogError(ex, "[MoonrakerPoller] Error stopping timelapse");
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
                        _logger.LogError(ex, "[MoonrakerPoller] Error");
                    }

                    // Keep-alive: In manual mode, ensure a continuous local stream is running even when not printing
                    try
                    {
                        var autoBroadcastEnabledLoop = _config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true;
                        if (!autoBroadcastEnabledLoop && lastState != "printing" && !_orchestrator.IsStreaming)
                        {
                            _logger.LogInformation("[MoonrakerPoller] Manual mode: ensuring local stream is running");
                            await _orchestrator.StartLocalStreamAsync(cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[MoonrakerPoller] Keep-alive stream error");
                    }

                    await Task.Delay(pollInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[MoonrakerPoller] Polling cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MoonrakerPoller] Unexpected error");
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
