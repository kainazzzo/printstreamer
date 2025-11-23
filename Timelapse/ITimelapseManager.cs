using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Timelapse
{
    public interface ITimelapseManager
    {
        Task<string?> StartTimelapseAsync(string sessionName, string? moonrakerFilename = null);
        Task<string?> StopTimelapseAsync(string sessionName);
        void NotifyPrintProgress(string? sessionName, int? currentLayer, int? totalLayers);
        /// <summary>
        /// Notify the manager of the current printer state (e.g., "printing", "paused") for a named session.
        /// When paused, timelapse capture should be disabled but the session should remain active.
        /// </summary>
        void NotifyPrinterState(string? sessionName, string? state);
    }
}
