using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PrintStreamer.Interfaces;

namespace PrintStreamer.Services;

/// <summary>
/// Monitors Stream:Mix:Enabled configuration and manages the mix ffmpeg process lifecycle.
/// When mix is disabled, terminates both the mix process AND any active YouTube broadcast.
/// </summary>
public class MixStreamHostedService : IHostedService, IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<MixStreamHostedService> _logger;
    private readonly IStreamOrchestrator _orchestrator;
    private Timer? _configMonitorTimer;
    private bool? _lastKnownState;
    private readonly object _lock = new();
    private int? _lastMixProcessId;

    public MixStreamHostedService(
        IConfiguration config,
        ILogger<MixStreamHostedService> logger,
        IStreamOrchestrator orchestrator)
    {
        _config = config;
        _logger = logger;
        _orchestrator = orchestrator;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[MixStreamHostedService] Starting mix stream monitoring");
        
        // Start timer to check config every 2 seconds
        _configMonitorTimer = new Timer(
            callback: _ => CheckAndUpdateMixState(),
            state: null,
            dueTime: TimeSpan.FromSeconds(1), // Initial check after 1 second
            period: TimeSpan.FromSeconds(2)
        );

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[MixStreamHostedService] Stopping mix stream monitoring");
        
        _configMonitorTimer?.Dispose();
        
        // Clean up any mix process on shutdown
        TerminateMixProcess();
        
        return Task.CompletedTask;
    }

    private void CheckAndUpdateMixState()
    {
        try
        {
            lock (_lock)
            {
                var mixEnabled = _config.GetValue<bool?>("Stream:Mix:Enabled") ?? true;

                if (_lastKnownState == mixEnabled)
                {
                    // No change, but still check if tracked process is dead
                    if (!mixEnabled && _lastMixProcessId.HasValue)
                    {
                        VerifyMixProcessTerminated();
                    }
                    return;
                }

                _lastKnownState = mixEnabled;

                if (mixEnabled)
                {
                    _logger.LogInformation("[MixStreamHostedService] Stream:Mix:Enabled changed to true - mix endpoint will serve on next request");
                }
                else
                {
                    _logger.LogInformation("[MixStreamHostedService] Stream:Mix:Enabled changed to false - TERMINATING MIX PROCESS AND YOUTUBE BROADCAST");
                    TerminateMixProcess();
                    
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (_orchestrator.IsBroadcastActive)
                            {
                                _logger.LogInformation("[MixStreamHostedService] Stopping active YouTube broadcast...");
                                var (ok, msg) = await _orchestrator.StopBroadcastAsync(CancellationToken.None);
                                _logger.LogInformation("[MixStreamHostedService] Broadcast stop result: {Success} - {Message}", ok, msg);
                            }
                            else
                            {
                                _logger.LogInformation("[MixStreamHostedService] No active broadcast to stop");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[MixStreamHostedService] Error stopping broadcast");
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MixStreamHostedService] Error checking mix state");
        }
    }

    private void TerminateMixProcess()
    {
        try
        {
            if (_lastMixProcessId.HasValue)
            {
                try
                {
                    var proc = Process.GetProcessById(_lastMixProcessId.Value);
                    if (!proc.HasExited)
                    {
                        _logger.LogInformation("[MixStreamHostedService] Terminating mix ffmpeg process (PID: {ProcessId})", _lastMixProcessId.Value);
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                        _logger.LogInformation("[MixStreamHostedService] Mix process terminated");
                    }
                    proc.Dispose();
                }
                catch (ArgumentException)
                {
                    // Process no longer exists
                    _logger.LogDebug("[MixStreamHostedService] Mix process {ProcessId} no longer exists", _lastMixProcessId.Value);
                }
                catch (InvalidOperationException)
                {
                    // Process was already disposed
                    _logger.LogDebug("[MixStreamHostedService] Mix process {ProcessId} already disposed", _lastMixProcessId.Value);
                }
            }

            // Also attempt to find and kill any other ffmpeg processes that might be mix-related
            // This is a fallback in case the PID tracking fails
            KillMixRelatedFfmpegProcesses();
        }
        finally
        {
            _lastMixProcessId = null;
        }
    }

    private void VerifyMixProcessTerminated()
    {
        if (!_lastMixProcessId.HasValue)
            return;

        try
        {
            var proc = Process.GetProcessById(_lastMixProcessId.Value);
            if (!proc.HasExited)
            {
                _logger.LogWarning("[MixStreamHostedService] Mix process (PID: {ProcessId}) is still running after disable - killing it", _lastMixProcessId.Value);
                proc.Kill(entireProcessTree: true);
            }
            proc.Dispose();
        }
        catch (ArgumentException)
        {
            // Already terminated - good
            _lastMixProcessId = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MixStreamHostedService] Error verifying mix process termination");
        }
    }

    private void KillMixRelatedFfmpegProcesses()
    {
        try
        {
            // Find and kill ffmpeg processes that are reading from /stream/overlay and /stream/audio
            // These are mix processes
            var processes = Process.GetProcessesByName("ffmpeg");
            
            foreach (var proc in processes)
            {
                try
                {
                    // Skip if this is the process we're tracking
                    if (_lastMixProcessId.HasValue && proc.Id == _lastMixProcessId.Value)
                        continue;

                    // On Linux, we could read /proc/[pid]/cmdline to check the arguments
                    // For now, we'll be conservative and not kill unknown ffmpeg processes
                }
                catch
                {
                    // Ignore errors when checking processes
                }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            // Ignore errors when enumerating processes
        }
    }

    /// <summary>
    /// Called by MixStreamer when it starts to register its process ID for tracking
    /// </summary>
    internal void RegisterMixProcess(int processId)
    {
        lock (_lock)
        {
            _lastMixProcessId = processId;
            _logger.LogDebug("[MixStreamHostedService] Registered mix process PID: {ProcessId}", processId);
        }
    }

    public void Dispose()
    {
        _configMonitorTimer?.Dispose();
    }
}
