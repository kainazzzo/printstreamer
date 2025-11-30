using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Timelapse;
using System.IO;
using System.Linq;

namespace PrintStreamer.Endpoints.Api.Timelapses
{
    public class TimelapseFrameDeleteEndpoint : Endpoint<TimelapseNameFileRequest>
    {
        private readonly TimelapseManager _timelapseManager;

        public TimelapseFrameDeleteEndpoint(TimelapseManager timelapseManager)
        {
            _timelapseManager = timelapseManager;
        }

        public override void Configure()
        {
            Delete("/api/timelapses/{name}/frames/{filename}");
            AllowAnonymous();
        }

        public override async Task HandleAsync(TimelapseNameFileRequest req, CancellationToken ct)
        {
            try
            {
                var timelapseDir = Path.Combine(_timelapseManager.TimelapseDirectory, req.Name);
                if (!Directory.Exists(timelapseDir))
                {
                    HttpContext.Response.StatusCode = 404;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Timelapse not found" }, ct);
                    return;
                }

                if (req.Filename.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Invalid filename" }, ct);
                    return;
                }

                var filePath = Path.Combine(timelapseDir, req.Filename);
                if (!File.Exists(filePath))
                {
                    HttpContext.Response.StatusCode = 404;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "File not found" }, ct);
                    return;
                }

                if (!req.Filename.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase))
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Only frame .jpg files can be deleted" }, ct);
                    return;
                }

                if (_timelapseManager.GetActiveSessionNames().Contains(req.Name))
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Cannot delete frames while timelapse is active" }, ct);
                    return;
                }

                File.Delete(filePath);

                // Reindex remaining frames
                var remaining = Directory.GetFiles(timelapseDir, "frame_*.jpg").OrderBy(f => f).ToArray();
                for (int i = 0; i < remaining.Length; i++)
                {
                    var dst = Path.Combine(timelapseDir, $"frame_{i:D6}.jpg");
                    var src = remaining[i];
                    if (string.Equals(Path.GetFileName(src), Path.GetFileName(dst), System.StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                    }
                    catch { /* ignore */ }
                }

                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }

    public class TimelapseNameFileRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Filename { get; set; } = string.Empty;
    }
}
