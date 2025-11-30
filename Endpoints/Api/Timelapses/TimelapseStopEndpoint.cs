using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Timelapse;

namespace PrintStreamer.Endpoints.Api.Timelapses
{
    public class TimelapseStopEndpoint : Endpoint<TimelapseNameRequest>
    {
        private readonly TimelapseManager _timelapseManager;

        public TimelapseStopEndpoint(TimelapseManager timelapseManager)
        {
            _timelapseManager = timelapseManager;
        }

        public override void Configure()
        {
            Post("/api/timelapses/{name}/stop");
            AllowAnonymous();
        }

        public override async Task HandleAsync(TimelapseNameRequest req, CancellationToken ct)
        {
            try
            {
                var videoPath = await _timelapseManager.StopTimelapseAsync(req.Name);
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { success = true, videoPath }, ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
