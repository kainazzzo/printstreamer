using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Timelapse;

namespace PrintStreamer.Endpoints.Api.Timelapses
{
    public class TimelapseStartEndpoint : Endpoint<TimelapseNameRequest>
    {
        public override void Configure()
        {
            Post("/api/timelapses/{name}/start");
            AllowAnonymous();
        }

        public override async Task HandleAsync(TimelapseNameRequest req, CancellationToken ct)
        {
            try
            {
                var timelapseManager = HttpContext.RequestServices.GetRequiredService<TimelapseManager>();
                var sessionName = await timelapseManager.StartTimelapseAsync(req.Name);
                if (sessionName != null)
                {
                    HttpContext.Response.StatusCode = 200;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = true, sessionName }, ct);
                    return;
                }
                HttpContext.Response.StatusCode = 400;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Failed to start timelapse" }, ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
