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
    public class TimelapseFramesEndpoint : Endpoint<TimelapseNameRequest>
    {
        public override void Configure()
        {
            Get("/api/timelapses/{name}/frames");
            AllowAnonymous();
        }

        public override async Task HandleAsync(TimelapseNameRequest req, CancellationToken ct)
        {
            try
            {
                var timelapseManager = HttpContext.RequestServices.GetRequiredService<TimelapseManager>();
                var timelapseDir = Path.Combine(timelapseManager.TimelapseDirectory, req.Name);
                if (!Directory.Exists(timelapseDir))
                {
                    HttpContext.Response.StatusCode = 404;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Timelapse not found" }, ct);
                    return;
                }

                var frames = Directory.GetFiles(timelapseDir, "frame_*.jpg").OrderBy(f => f).Select(Path.GetFileName).ToArray();
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { success = true, frames }, ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }

    public class TimelapseNameRequest
    {
        public string Name { get; set; } = string.Empty;
    }
}
