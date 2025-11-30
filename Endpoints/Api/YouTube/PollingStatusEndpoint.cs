using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.YouTube
{
    public class PollingStatusEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Get("/api/youtube/polling/status"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var pm = HttpContext.RequestServices.GetRequiredService<YouTubePollingManager>();
            var stats = pm.GetStats();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(stats, ct);
        }
    }
}
