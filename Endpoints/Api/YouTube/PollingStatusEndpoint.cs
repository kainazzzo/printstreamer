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
        private readonly YouTubePollingManager _pollingManager;

        public PollingStatusEndpoint(YouTubePollingManager pollingManager)
        {
            _pollingManager = pollingManager;
        }

        public override void Configure() { Get("/api/youtube/polling/status"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var stats = _pollingManager.GetStats();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(stats, ct);
        }
    }
}
