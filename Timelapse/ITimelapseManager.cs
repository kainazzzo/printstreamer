using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Timelapse
{
    public interface ITimelapseManager
    {
        Task<string?> StartTimelapseAsync(string sessionName, string? moonrakerFilename = null);
        Task<string?> StopTimelapseAsync(string sessionName);

        /// <summary>
        /// Synchronous notification of print progress. This variant updates capture gating and stop flags
        /// but does not perform auto-finalization.
        /// </summary>
        void NotifyPrintProgress(string? sessionName, int? currentLayer, int? totalLayers);

        /// <summary>
        /// Asynchronous notification of print progress. This variant may perform auto-finalization
        /// (create video and stop session) depending on manager configuration.
        /// Returns the created video path if the manager performed finalization, otherwise null.
        /// </summary>
        Task<string?> NotifyPrintProgressAsync(string? sessionName, int? currentLayer, int? totalLayers);

        /// <summary>
        /// Notify the manager of the current printer state (e.g., "printing", "paused") for a named session.
        /// When paused, timelapse capture should be disabled but the session should remain active.
        /// </summary>
        void NotifyPrinterState(string? sessionName, string? state);
    }
}
