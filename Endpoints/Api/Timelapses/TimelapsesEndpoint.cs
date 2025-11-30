using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Timelapse;

namespace PrintStreamer.Endpoints.Api.Timelapses
{
    public class TimelapsesEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Get("/api/timelapses");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var timelapseManager = HttpContext.RequestServices.GetRequiredService<TimelapseManager>();
            var timelapses = timelapseManager.GetAllTimelapses();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(timelapses, ct);
        }
    }
}
