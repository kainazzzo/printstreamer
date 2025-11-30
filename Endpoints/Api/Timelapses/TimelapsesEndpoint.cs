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
        private readonly TimelapseManager _timelapseManager;

        public TimelapsesEndpoint(TimelapseManager timelapseManager)
        {
            _timelapseManager = timelapseManager;
        }

        public override void Configure()
        {
            Get("/api/timelapses");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var timelapses = _timelapseManager.GetAllTimelapses();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(timelapses, ct);
        }
    }
}
