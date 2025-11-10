using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintStreamer.Models;
using PrintStreamer.Timelapse;

namespace PrintStreamer.Services
{
    /// <summary>
    /// Orchestrates print streaming, timelapse capture, and YouTube broadcast based on printer state changes.
    /// Decoupled from the polling mechanism - subscribes to PrinterState events raised by MoonrakerPoller.
    /// 
    /// Responsibilities:
    /// - Start/stop ffmpeg stream when printing begins/ends
    /// - Manage timelapse session lifecycle
    /// - Create and manage YouTube broadcasts
    /// - Handle job filename changes and session transitions
    /// - Respect grace periods for offline/idle state handling
    /// </summary>
    internal class PrintStreamOrchestrator
    {
        private readonly IConfiguration _config;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<PrintStreamOrchestrator> _logger;
        private readonly TimelapseManager _timelapseManager;
        
        // State tracking
        private PrinterState? _lastPrinterState;
        private string? _activeTimelapseSession;
        private string? _activeTimelapseJobFilename;
        private bool _lastLayerTriggered;
        private DateTime _lastInfoSeenAt = DateTime.UtcNow;
        private DateTime? _lastPrintingSeenAt;
        private DateTime? _idleStateSince;
        private DateTime? _jobMissingSince;
        private bool _waitingForResumeLogged;
        
        // Grace period configuration
        private readonly TimeSpan _offlineGrace;
        private readonly TimeSpan _idleFinalizeDelay;
        private readonly int _lastLayerOffset;
        private readonly int _lastLayerRemainingSeconds;
        private readonly double _lastLayerProgressPercent;
        
        // State sets for determining active vs. done states
        private static readonly HashSet<string> ActiveStates = new(StringComparer.OrdinalIgnoreCase)
        {
            "printing", "paused", "resuming"
        };
        
        private static readonly HashSet<string> DoneStates = new(StringComparer.OrdinalIgnoreCase)
        {
            "idle", "complete", "stopped", "error", "standby"
        };

        public PrintStreamOrchestrator(IConfiguration config, ILoggerFactory loggerFactory, TimelapseManager timelapseManager)
        {
            _config = config;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<PrintStreamOrchestrator>();
            _timelapseManager = timelapseManager;
            
            // Read grace period configuration
            _offlineGrace = _config.GetValue<TimeSpan?>("Timelapse:OfflineGracePeriod") ?? TimeSpan.FromMinutes(10);
            _idleFinalizeDelay = _config.GetValue<TimeSpan?>("Timelapse:IdleFinalizeDelay") ?? TimeSpan.FromSeconds(20);
            _lastLayerOffset = _config.GetValue<int?>("Timelapse:LastLayerOffset") ?? 1;
            _lastLayerRemainingSeconds = _config.GetValue<int?>("Timelapse:LastLayerRemainingSeconds") ?? 30;
            _lastLayerProgressPercent = _config.GetValue<double?>("Timelapse:LastLayerProgressPercent") ?? 98.5;
        }

        /// <summary>
        /// Called when printer state changes (from MoonrakerPoller event).
        /// This is the main entry point for all orchestration logic.
        /// </summary>
        public async Task HandlePrinterStateChangedAsync(PrinterState? previous, PrinterState? current, CancellationToken cancellationToken)
        {
            try
            {
                if (current == null)
                    return;

                var stateStr = current.State ?? "unknown";
                var jobStr = current.Filename ?? "(no job)";
                var progressStr = current.ProgressPercent?.ToString("F1") ?? "n/a";
                
                _logger.LogDebug("[PrintStreamOrchestrator] Printer state: {State}, Job: {Job}, Progress: {Progress}%",
                    stateStr, jobStr, progressStr);

                // Update tracking timestamps
                _lastInfoSeenAt = DateTime.UtcNow;
                _lastPrinterState = current;

                // Track printing state transitions
                if (IsActivelyPrinting(current))
                {
                    _lastPrintingSeenAt = DateTime.UtcNow;
                    _idleStateSince = null;
                    _waitingForResumeLogged = false;
                }
                else if (IsDone(current))
                {
                    _idleStateSince ??= DateTime.UtcNow;
                }

                // Track job changes and missing job
                if (_activeTimelapseSession != null)
                {
                    if (string.IsNullOrWhiteSpace(current.Filename))
                    {
                        _jobMissingSince ??= DateTime.UtcNow;
                    }
                    else
                    {
                        _jobMissingSince = null;
                    }

                    // Capture job filename when session starts
                    if (string.IsNullOrWhiteSpace(_activeTimelapseJobFilename) && !string.IsNullOrWhiteSpace(current.Filename))
                    {
                        _activeTimelapseJobFilename = current.Filename;
                    }
                }
                else
                {
                    _jobMissingSince = null;
                }

                // Detect job change while timelapse is active - force finalize old session
                bool forceFinalizeActiveSession = false;
                if (IsActivelyPrinting(current) && _activeTimelapseSession != null && !string.IsNullOrWhiteSpace(current.Filename))
                {
                    if (!string.IsNullOrWhiteSpace(_activeTimelapseJobFilename) &&
                        !string.Equals(current.Filename, _activeTimelapseJobFilename, StringComparison.OrdinalIgnoreCase))
                    {
                        forceFinalizeActiveSession = true;
                        _logger.LogInformation(
                            "[PrintStreamOrchestrator] Job changed while timelapse active: {OldJob} → {NewJob}. Finalizing {Session}",
                            _activeTimelapseJobFilename, current.Filename, _activeTimelapseSession);
                    }
                }

                // Update timelapse progress
                if (_activeTimelapseSession != null)
                {
                    try
                    {
                        _timelapseManager.NotifyPrintProgress(_activeTimelapseSession, current.CurrentLayer, current.TotalLayers);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[PrintStreamOrchestrator] Failed to notify timelapse of progress");
                    }
                }

                // Check for last-layer early finalize
                await CheckAndHandleLastLayerAsync(current, cancellationToken);

                // Handle print completion or forced finalize
                if (forceFinalizeActiveSession || IsDone(current) || (IsActivelyPrinting(current) && _activeTimelapseSession == null))
                {
                    await EvaluateAndHandleFinalizationAsync(current, forceFinalizeActiveSession, cancellationToken);
                }
                
                // Start new broadcast/timelapse if printing and not already active
                if (!forceFinalizeActiveSession && IsActivelyPrinting(current) && _activeTimelapseSession == null)
                {
                    await StartPrintStreamAsync(current, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PrintStreamOrchestrator] Error handling printer state change");
            }
        }

        private async Task StartPrintStreamAsync(PrinterState state, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("[PrintStreamOrchestrator] Print started: {Job}", state.Filename ?? "(unknown)");

                // Start timelapse
                var jobNameSafe = SanitizeFilename(!string.IsNullOrWhiteSpace(state.Filename)
                    ? state.Filename
                    : $"printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}");

                _activeTimelapseSession = await _timelapseManager.StartTimelapseAsync(jobNameSafe, state.Filename);
                if (_activeTimelapseSession != null)
                {
                    _activeTimelapseJobFilename = state.Filename;
                    _lastLayerTriggered = false;
                    _logger.LogInformation("[PrintStreamOrchestrator] Timelapse session started: {Session}", _activeTimelapseSession);
                }
                else
                {
                    _logger.LogWarning("[PrintStreamOrchestrator] Failed to start timelapse session");
                    return;
                }

                // Start YouTube broadcast if enabled
                bool autoBroadcastEnabled = _config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true;
                if (autoBroadcastEnabled && !MoonrakerPoller.IsBroadcastActive)
                {
                    try
                    {
                        _logger.LogInformation("[PrintStreamOrchestrator] Starting YouTube broadcast...");
                        var (success, message, broadcastId) = await MoonrakerPoller.StartBroadcastAsync(_config, _loggerFactory, cancellationToken);
                        if (success)
                        {
                            _logger.LogInformation("[PrintStreamOrchestrator] Broadcast started: {BroadcastId}", broadcastId);
                        }
                        else
                        {
                            _logger.LogWarning("[PrintStreamOrchestrator] Failed to start broadcast: {Message}", message);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[PrintStreamOrchestrator] Error starting broadcast");
                    }
                }
                else if (!autoBroadcastEnabled)
                {
                    _logger.LogInformation("[PrintStreamOrchestrator] Auto-broadcast disabled; manual mode");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PrintStreamOrchestrator] Failed to start print stream");
            }
        }

        private Task CheckAndHandleLastLayerAsync(PrinterState state, CancellationToken cancellationToken)
        {
            if (!IsActivelyPrinting(state) || _activeTimelapseSession == null || _lastLayerTriggered)
                return Task.CompletedTask;

            bool lastLayerByTime = state.Remaining.HasValue && state.Remaining.Value <= TimeSpan.FromSeconds(_lastLayerRemainingSeconds);
            bool lastLayerByProgress = state.ProgressPercent.HasValue && state.ProgressPercent.Value >= _lastLayerProgressPercent;
            bool lastLayerByLayer = state.CurrentLayer.HasValue && state.TotalLayers.HasValue &&
                                    state.TotalLayers.Value > 0 &&
                                    state.CurrentLayer.Value >= (state.TotalLayers.Value - _lastLayerOffset);

            if (lastLayerByTime || lastLayerByProgress || lastLayerByLayer)
            {
                _logger.LogInformation("[PrintStreamOrchestrator] Last-layer detected - finalizing timelapse now");
                _lastLayerTriggered = true;

                // Finalize timelapse in background
                var sessionToFinalize = _activeTimelapseSession;
                _ = Task.Run(async () => await FinalizeTimelapseSessionAsync(sessionToFinalize, cancellationToken), CancellationToken.None);

                // Clear active session so end-of-print won't double-finalize
                _activeTimelapseSession = null;
                _activeTimelapseJobFilename = null;
            }

            return Task.CompletedTask;
        }

        private async Task EvaluateAndHandleFinalizationAsync(PrinterState state, bool forceFinalizeActiveSession, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;

            bool layersComplete = state.CurrentLayer.HasValue && state.TotalLayers.HasValue && state.TotalLayers.Value > 0 &&
                                  state.CurrentLayer.Value >= Math.Max(0, state.TotalLayers.Value - _lastLayerOffset);
            bool progressComplete = state.ProgressPercent.HasValue && state.ProgressPercent.Value >= 99.0;
            bool idleMet = _idleStateSince.HasValue && (now - _idleStateSince.Value) >= _idleFinalizeDelay;
            bool jobMissingMet = _jobMissingSince.HasValue && (now - _jobMissingSince.Value) >= _offlineGrace;
            bool offlineMet = _lastPrintingSeenAt.HasValue && (now - _lastPrintingSeenAt.Value) >= _offlineGrace && 
                              (now - _lastInfoSeenAt) >= _offlineGrace;

            var shouldFinalize = forceFinalizeActiveSession || layersComplete || progressComplete || idleMet || jobMissingMet || offlineMet;

            if (!shouldFinalize)
            {
                if (!_waitingForResumeLogged)
                {
                    _logger.LogDebug(
                        "[PrintStreamOrchestrator] Holding timelapse (state={State}, progress={Progress}%, idle={Idle}, jobMissing={JobMissing}, offline={Offline})",
                        state.State ?? "n/a",
                        state.ProgressPercent?.ToString("F1") ?? "n/a",
                        _idleStateSince.HasValue ? (now - _idleStateSince.Value).ToString(@"hh\:mm\:ss") : "n/a",
                        _jobMissingSince.HasValue ? (now - _jobMissingSince.Value).ToString(@"hh\:mm\:ss") : "n/a",
                        _lastPrintingSeenAt.HasValue ? (now - _lastPrintingSeenAt.Value).ToString(@"hh\:mm\:ss") : "n/a");
                    _waitingForResumeLogged = true;
                }
                return;
            }

            _waitingForResumeLogged = false;

            var jobLabel = _lastPrinterState?.Filename ?? _activeTimelapseSession ?? "(unknown)";
            if (forceFinalizeActiveSession)
            {
                _logger.LogInformation("[PrintStreamOrchestrator] Finalizing timelapse before new job: {Job}", jobLabel);
            }
            else
            {
                _logger.LogInformation("[PrintStreamOrchestrator] Print finished: {Job}", jobLabel);
            }

            // Stop broadcast if appropriate
            if (!forceFinalizeActiveSession)
            {
                bool endStreamAfterPrint = _config.GetValue<bool?>("YouTube:LiveBroadcast:EndStreamAfterPrint") ?? true;
                if (endStreamAfterPrint && MoonrakerPoller.IsBroadcastActive)
                {
                    try
                    {
                        var (ok, msg) = await MoonrakerPoller.StopBroadcastAsync(_config, cancellationToken, _loggerFactory);
                        if (ok)
                        {
                            _logger.LogInformation("[PrintStreamOrchestrator] Broadcast stopped");
                        }
                        else
                        {
                            _logger.LogWarning("[PrintStreamOrchestrator] Error stopping broadcast: {Message}", msg);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[PrintStreamOrchestrator] Error stopping broadcast");
                    }
                }
                else if (MoonrakerPoller.IsBroadcastActive)
                {
                    _logger.LogInformation("[PrintStreamOrchestrator] Leaving broadcast running (EndStreamAfterPrint=false)");
                }
            }

            // Finalize timelapse
            if (_activeTimelapseSession != null)
            {
                await FinalizeTimelapseSessionAsync(_activeTimelapseSession, cancellationToken);
            }

            // Reset state
            _activeTimelapseSession = null;
            _activeTimelapseJobFilename = null;
            _jobMissingSince = null;
            _idleStateSince = null;
            _lastPrintingSeenAt = null;
        }

        private async Task FinalizeTimelapseSessionAsync(string sessionName, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("[PrintStreamOrchestrator] Stopping timelapse: {Session}", sessionName);
                var createdVideoPath = await _timelapseManager.StopTimelapseAsync(sessionName);

                if (!string.IsNullOrWhiteSpace(createdVideoPath) && File.Exists(createdVideoPath))
                {
                    _logger.LogInformation("[PrintStreamOrchestrator] Timelapse video created: {Path}", createdVideoPath);
                    
                    // TODO: Upload to YouTube if enabled
                    bool uploadEnabled = _config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false;
                    if (uploadEnabled)
                    {
                        _logger.LogInformation("[PrintStreamOrchestrator] Timelapse upload enabled (future implementation)");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PrintStreamOrchestrator] Failed to finalize timelapse: {Session}", sessionName);
            }
        }

        private bool IsActivelyPrinting(PrinterState state)
        {
            return !string.IsNullOrWhiteSpace(state.State) && ActiveStates.Contains(state.State);
        }

        private bool IsDone(PrinterState state)
        {
            return !string.IsNullOrWhiteSpace(state.State) && DoneStates.Contains(state.State);
        }

        private static string SanitizeFilename(string filename)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(filename.Where(c => !invalidChars.Contains(c)).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "timelapse" : sanitized;
        }

        public void Dispose()
        {
            // Cleanup if needed
        }
    }
}
