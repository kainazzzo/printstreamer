using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Services
{
    /// <summary>
    /// Manages the lifecycle of overlay ffmpeg processes.
    /// Tracks processes and provides methods to terminate them when overlay is disabled.
    /// </summary>
    public class OverlayProcessService
    {
        private readonly ILogger<OverlayProcessService> _logger;
        private readonly ConcurrentDictionary<int, Process> _processes = new();
        private readonly object _lock = new();

        public OverlayProcessService(ILogger<OverlayProcessService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Register a new overlay ffmpeg process for tracking
        /// </summary>
        public void RegisterProcess(Process process)
        {
            if (process == null || process.HasExited) return;

            _processes.TryAdd(process.Id, process);
            _logger.LogDebug("[OverlayProcessService] Registered overlay process PID {PID}", process.Id);
        }

        /// <summary>
        /// Terminate all tracked overlay ffmpeg processes
        /// </summary>
        public void TerminateAllProcesses()
        {
            lock (_lock)
            {
                var pids = _processes.Keys.ToList();
                _logger.LogInformation("[OverlayProcessService] Terminating {Count} overlay ffmpeg process(es)", pids.Count);

                foreach (var pid in pids)
                {
                    if (_processes.TryRemove(pid, out var proc))
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                _logger.LogInformation("[OverlayProcessService] Killing overlay ffmpeg PID {PID}", pid);
                                proc.Kill(entireProcessTree: true);
                                proc.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[OverlayProcessService] Failed to kill overlay process PID {PID}", pid);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Clean up exited processes from tracking
        /// </summary>
        public void CleanupExitedProcesses()
        {
            var exitedPids = _processes
                .Where(kvp => kvp.Value.HasExited)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var pid in exitedPids)
            {
                if (_processes.TryRemove(pid, out var proc))
                {
                    try { proc.Dispose(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            TerminateAllProcesses();
        }
    }
}
