using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Timelapse
{
    public interface ITimelapseManager
    {
        Task<string?> StartTimelapseAsync(string sessionName, string? moonrakerFilename = null);
        Task<string?> StopTimelapseAsync(string sessionName);
        void NotifyPrintProgress(string? sessionName, int? currentLayer, int? totalLayers);
    }
}
