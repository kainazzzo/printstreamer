using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrintStreamer.Models;
using PrintStreamer.Timelapse;
using PrintStreamer.Interfaces;

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
    internal class PrintStreamOrchestrator : IDisposable
    {
        private readonly IConfiguration _config;
        private readonly ILogger<PrintStreamOrchestrator> _logger;
        private readonly ITimelapseManager _timelapseManager;
        private readonly IStreamOrchestrator _streamOrchestrator;
        private readonly Dictionary<string, string?> _sessionJobMap = new();
        private readonly IMoonrakerPoller _moonrakerPoller;
        
        // State tracking
        private PrinterState? _lastPrinterState;
        private string? _activeTimelapseSession;
        private string? _activeTimelapseJobFilename;
        private string? _timelapseFinalizedForJob;
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

        public PrintStreamOrchestrator(IConfiguration config, ITimelapseManager timelapseManager, IStreamOrchestrator streamOrchestrator, IMoonrakerPoller moonrakerPoller, ILogger<PrintStreamOrchestrator> logger)
        {
            _config = config;

            _timelapseManager = timelapseManager;
            _streamOrchestrator = streamOrchestrator;
            _logger = logger;
            
            _offlineGrace = _config.GetValue<TimeSpan?>("Timelapse:OfflineGracePeriod") ?? TimeSpan.FromMinutes(10);
            _idleFinalizeDelay = _config.GetValue<TimeSpan?>("Timelapse:IdleFinalizeDelay") ?? TimeSpan.FromSeconds(20);
            _lastLayerOffset = _config.GetValue<int?>("Timelapse:LastLayerOffset") ?? 1;
            _lastLayerRemainingSeconds = _config.GetValue<int?>("Timelapse:LastLayerRemainingSeconds") ?? 30;
            _lastLayerProgressPercent = _config.GetValue<double?>("Timelapse:LastLayerProgressPercent") ?? 98.5;
            _moonrakerPoller = moonrakerPoller;
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

                // If we previously finalized a timelapse for a job and now see a different active job,
                // clear the finalized marker so a new timelapse can be started for the new job.
                if (!string.IsNullOrWhiteSpace(_timelapseFinalizedForJob) &&
                    !string.IsNullOrWhiteSpace(current.Filename) &&
                    !string.Equals(current.Filename, _timelapseFinalizedForJob, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("[PrintStreamOrchestrator] Clearing finalized timelapse marker for job {OldJob} because new job {NewJob} was detected",
                        _timelapseFinalizedForJob, current.Filename);
                    _timelapseFinalizedForJob = null;
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

                // Update timelapse progress (use async variant so manager can optionally auto-finalize)
                if (_activeTimelapseSession != null)
                {
                    try
                    {
                        var createdByManager = await _timelapseManager.NotifyPrintProgressAsync(_activeTimelapseSession, current.CurrentLayer, current.TotalLayers);
                        // Inform timelapse manager about printer state (paused/resuming/printing)
                        _timelapseManager.NotifyPrinterState(_activeTimelapseSession, current.State);

                        if (!string.IsNullOrWhiteSpace(createdByManager))
                        {
                            // Manager already finalized the session and created the video.
                            _logger.LogInformation("[PrintStreamOrchestrator] Timelapse manager auto-finalized session {Session}. Video: {Video}", _activeTimelapseSession, createdByManager);
                            _timelapseFinalizedForJob = _activeTimelapseJobFilename;
                            // Clear active session so subsequent orchestration won't double-finalize.
                            _activeTimelapseSession = null;
                            _activeTimelapseJobFilename = null;
                        }
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

                // After starting a session, immediately notify the timelapse manager of progress
                // so the manager can auto-finalize if it wants. If the manager does not auto-finalize
                // (returns null) and we're already at the last-layer, finalize the session ourselves.
                if (_activeTimelapseSession != null)
                {
                    try
                    {
                        var createdByManager = await _timelapseManager.NotifyPrintProgressAsync(_activeTimelapseSession, current.CurrentLayer, current.TotalLayers);
                        _timelapseManager.NotifyPrinterState(_activeTimelapseSession, current.State);

                        if (!string.IsNullOrWhiteSpace(createdByManager))
                        {
                            _logger.LogInformation("[PrintStreamOrchestrator] Timelapse manager auto-finalized session {Session} on startup notification. Video: {Video}", _activeTimelapseSession, createdByManager);
                            _timelapseFinalizedForJob = _activeTimelapseJobFilename;
                            _activeTimelapseSession = null;
                            _activeTimelapseJobFilename = null;
                        }
                        else
                        {
                            // Manager did not auto-finalize. If the current state indicates we're at the
                            // last layer, finalize now instead of waiting for a background trigger.
                            bool lastLayerByTime = current.Remaining.HasValue && current.Remaining.Value <= TimeSpan.FromSeconds(_lastLayerRemainingSeconds);
                            bool lastLayerByProgress = current.ProgressPercent.HasValue && current.ProgressPercent.Value >= _lastLayerProgressPercent;
                            bool lastLayerByLayer = current.CurrentLayer.HasValue && current.TotalLayers.HasValue &&
                                                    current.TotalLayers.Value > 0 &&
                                                    current.CurrentLayer.Value >= (current.TotalLayers.Value - _lastLayerOffset);

                            if (lastLayerByTime || lastLayerByProgress || lastLayerByLayer)
                            {
                                var sessionToFinalizeNow = _activeTimelapseSession;
                                _logger.LogInformation("[PrintStreamOrchestrator] Manager did not auto-finalize; last-layer detected after starting session - finalizing timelapse now for session {Session}", sessionToFinalizeNow);

                                // Finalize synchronously here to ensure tests that expect StopTimelapseAsync to be called see it.
                                await FinalizeTimelapseSessionAsync(sessionToFinalizeNow!, cancellationToken);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[PrintStreamOrchestrator] Failed to notify timelapse manager after starting session");
                    }
                }

                // Re-check last-layer now that a session may have been created by StartPrintStreamAsync.
                await CheckAndHandleLastLayerAsync(current, cancellationToken);
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

                // If we've already finalized a timelapse for this exact job filename, don't start a new one.
                if (!string.IsNullOrWhiteSpace(state.Filename) && !string.IsNullOrWhiteSpace(_timelapseFinalizedForJob) &&
                    string.Equals(state.Filename, _timelapseFinalizedForJob, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[PrintStreamOrchestrator] Timelapse already finalized for this job; skipping new timelapse: {Job}", state.Filename);
                    return;
                }

                // Start timelapse
                var jobNameSafe = SanitizeFilename(!string.IsNullOrWhiteSpace(state.Filename)
                    ? state.Filename
                    : $"printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}");

                _logger.LogDebug("[PrintStreamOrchestrator] About to start timelapse (jobSafe={JobSafe}, filename={Filename})", jobNameSafe, state.Filename);

                _activeTimelapseSession = await _timelapseManager.StartTimelapseAsync(jobNameSafe, state.Filename);
                if (_activeTimelapseSession != null)
                {
                    _activeTimelapseJobFilename = state.Filename;
                    _lastLayerTriggered = false;
                    _sessionJobMap[_activeTimelapseSession] = _activeTimelapseJobFilename;
                    _logger.LogDebug("[PrintStreamOrchestrator] Session->job map set: {Session} => {Job}", _activeTimelapseSession, _activeTimelapseJobFilename);
                    _logger.LogInformation("[PrintStreamOrchestrator] Timelapse session started: {Session}", _activeTimelapseSession);

                    // Immediately notify the timelapse manager about current progress so it can auto-finalize.
                    // This guarantees that tests which expect StopTimelapseAsync to be called when the manager
                    // does not auto-finalize will observe the call synchronously.
                    try
                    {
                        var createdByManager = await _timelapseManager.NotifyPrintProgressAsync(_activeTimelapseSession, state.CurrentLayer, state.TotalLayers);
                        _timelapseManager.NotifyPrinterState(_activeTimelapseSession, state.State);

                        if (!string.IsNullOrWhiteSpace(createdByManager))
                        {
                            _logger.LogInformation("[PrintStreamOrchestrator] Timelapse manager auto-finalized session {Session} on startup notification. Video: {Video}", _activeTimelapseSession, createdByManager);
                            _timelapseFinalizedForJob = _activeTimelapseJobFilename;
                            // Clear active session so subsequent orchestration won't double-finalize.
                            _activeTimelapseSession = null;
                            _activeTimelapseJobFilename = null;
                            return;
                        }

                        // Manager did not auto-finalize. If we're already at the last-layer, finalize now
                        // to avoid any race with background finalizers.
                        bool lastLayerByTime = state.Remaining.HasValue && state.Remaining.Value <= TimeSpan.FromSeconds(_lastLayerRemainingSeconds);
                        bool lastLayerByProgress = state.ProgressPercent.HasValue && state.ProgressPercent.Value >= _lastLayerProgressPercent;
                        bool lastLayerByLayer = state.CurrentLayer.HasValue && state.TotalLayers.HasValue &&
                                                state.TotalLayers.Value > 0 &&
                                                state.CurrentLayer.Value >= (state.TotalLayers.Value - _lastLayerOffset);

                        if (lastLayerByTime || lastLayerByProgress || lastLayerByLayer)
                        {
                            var sessionToFinalizeNow = _activeTimelapseSession;
                            _logger.LogInformation("[PrintStreamOrchestrator] Manager did not auto-finalize; last-layer detected after starting session - finalizing timelapse now for session {Session}", sessionToFinalizeNow);

                            await FinalizeTimelapseSessionAsync(sessionToFinalizeNow!, cancellationToken);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[PrintStreamOrchestrator] Failed to notify timelapse manager after starting session");
                    }
                }
                else
                {
                    _logger.LogWarning("[PrintStreamOrchestrator] Failed to start timelapse session");
                    return;
                }

                // Start YouTube broadcast if enabled
                bool autoBroadcastEnabled = _config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true;
                if (autoBroadcastEnabled && !_streamOrchestrator.IsBroadcastActive)
                {
                    try
                    {
                        bool created = false;
                        string? message = null;
                        string? broadcastId = null;

                        if (_streamOrchestrator != null)
                        {
                            _logger.LogInformation("[PrintStreamOrchestrator] Starting YouTube broadcast via StreamOrchestrator...");
                            var (success, m, b) = await _streamOrchestrator.StartBroadcastAsync(cancellationToken);
                            created = success;
                            message = m;
                            broadcastId = b;
                            if (created)
                            {
                                _logger.LogInformation("[PrintStreamOrchestrator] Broadcast started: {BroadcastId}", broadcastId);
                            }
                        }
                        else
                        {
                            _logger.LogInformation("[PrintStreamOrchestrator] Starting YouTube broadcast via MoonrakerPoller (fallback)...");
                            var (success, m, b) = await _moonrakerPoller.StartBroadcastAsync(cancellationToken);
                            created = success;
                            message = m;
                            broadcastId = b;
                            if (created)
                            {
                                _logger.LogInformation("[PrintStreamOrchestrator] Broadcast started: {BroadcastId}", broadcastId);
                            }
                        }
                        if (created)
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
                _logger.LogDebug("[PrintStreamOrchestrator] Scheduling background finalization for session {Session}, job {Job}", sessionToFinalize, _activeTimelapseJobFilename);
                _ = Task.Run(async () => await FinalizeTimelapseSessionAsync(sessionToFinalize, cancellationToken), CancellationToken.None);

                // Clear active session so end-of-print won't double-finalize.
                // Remember which job this timelapse belonged to so we don't immediately restart for the same job.
                _timelapseFinalizedForJob = _activeTimelapseJobFilename;
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
                if (endStreamAfterPrint && _streamOrchestrator.IsBroadcastActive)
                {
                    try
                    {
                        bool stopped = false;
                        string? stopMessage = null;

                        
                            var (ok, msg) = await _streamOrchestrator.StopBroadcastAsync(cancellationToken);
                            stopped = ok;
                            stopMessage = msg;
                            if (stopped) _logger.LogInformation("[PrintStreamOrchestrator] Broadcast stopped");
                      
                        
                        if (stopped)
                        {
                            _logger.LogInformation("[PrintStreamOrchestrator] Broadcast stopped");
                        }
                        else
                        {
                            _logger.LogWarning("[PrintStreamOrchestrator] Error stopping broadcast: {Message}", stopMessage);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[PrintStreamOrchestrator] Error stopping broadcast");
                    }
                }
                else if (_streamOrchestrator.IsBroadcastActive)
                {
                    _logger.LogInformation("[PrintStreamOrchestrator] Leaving broadcast running (EndStreamAfterPrint=false)");
                }
            }

             // Finalize timelapse
            if (_activeTimelapseSession != null)
            {
                _timelapseFinalizedForJob = _activeTimelapseJobFilename;
                _logger.LogDebug("[PrintStreamOrchestrator] Finalizing active session {Session} for job {Job} (forceFinalize={Force})", _activeTimelapseSession, _activeTimelapseJobFilename, forceFinalizeActiveSession);
                await FinalizeTimelapseSessionAsync(_activeTimelapseSession, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(_lastPrinterState?.Filename))
            {
                // If active session was unexpectedly cleared (e.g., background finalize already cleared state),
                // attempt to finalize any session(s) associated with the same job filename from the session->job map.
                var jobToFinalize = _lastPrinterState.Filename!;
                var sessionsToFinalize = _sessionJobMap.Where(kv => !string.IsNullOrWhiteSpace(kv.Value) &&
                                                                     string.Equals(kv.Value, jobToFinalize, StringComparison.OrdinalIgnoreCase))
                                                       .Select(kv => kv.Key)
                                                       .ToList();
                foreach (var session in sessionsToFinalize)
                {
                    _logger.LogDebug("[PrintStreamOrchestrator] Finalizing mapped session {Session} for job {Job}", session, jobToFinalize);
                    await FinalizeTimelapseSessionAsync(session, cancellationToken);
                }
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
                _logger.LogDebug("[PrintStreamOrchestrator] Calling StopTimelapseAsync for session {Session}", sessionName);
                var createdVideoPath = await _timelapseManager.StopTimelapseAsync(sessionName);

                // Ensure finalized-job marker is set even if the orchestrator cleared active session state
                // (for example when finalization was kicked off in background). Use the session->job map
                // populated when the session started as a reliable source of truth.
                if (string.IsNullOrWhiteSpace(_timelapseFinalizedForJob) && !string.IsNullOrWhiteSpace(sessionName))
                {
                    if (_sessionJobMap.TryGetValue(sessionName, out var job))
                    {
                        _logger.LogDebug("[PrintStreamOrchestrator] Found job {Job} for session {Session} in session->job map", job, sessionName);
                        _timelapseFinalizedForJob = job;
                    }
                }
                // Remove mapping for this session now that it's finalized
                _sessionJobMap.Remove(sessionName);
 
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
