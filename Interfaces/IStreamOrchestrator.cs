namespace PrintStreamer.Services
{
    /// <summary>
    /// Interface for streaming orchestration functionality
    /// </summary>
    public interface IStreamOrchestrator
    {
        /// <summary>
        /// Gets whether a YouTube broadcast is currently active
        /// </summary>
        bool IsBroadcastActive { get; }

        /// <summary>
        /// Starts a YouTube broadcast
        /// </summary>
        Task<(bool success, string? message, string? broadcastId)> StartBroadcastAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Stops the current broadcast
        /// </summary>
        Task<(bool success, string? message)> StopBroadcastAsync(CancellationToken cancellationToken);
    }
}