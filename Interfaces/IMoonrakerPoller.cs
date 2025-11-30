using System;
using System.Threading;
using System.Threading.Tasks;
using PrintStreamer.Services;

namespace PrintStreamer.Interfaces
{
    internal interface IMoonrakerPoller
    {
        Task<(bool success, string? message, string? broadcastId)> StartBroadcastAsync(CancellationToken cancellationToken);
        void RegisterPrintStreamOrchestrator(PrintStreamOrchestrator orchestrator);
        Task PollAndStreamJobsAsync(CancellationToken cancellationToken);
    }
}