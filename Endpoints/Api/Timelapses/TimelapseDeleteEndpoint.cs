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
    public class TimelapseDeleteEndpoint : Endpoint<TimelapseNameRequest>
    {
        public override void Configure()
        {
            Delete("/api/timelapses/{name}");
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

                if (timelapseManager.GetActiveSessionNames().Contains(req.Name))
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Cannot delete active timelapse" }, ct);
                    return;
                }

                Directory.Delete(timelapseDir, true);
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
}
