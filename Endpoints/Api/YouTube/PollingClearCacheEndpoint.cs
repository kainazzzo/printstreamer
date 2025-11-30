using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.YouTube
{
    public class PollingClearCacheEndpoint : EndpointWithoutRequest<object>
    {
        private readonly YouTubePollingManager _pollingManager;

        public PollingClearCacheEndpoint(YouTubePollingManager pollingManager)
        {
            _pollingManager = pollingManager;
        }

        public override void Configure() { Post("/api/youtube/polling/clear-cache"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            _pollingManager.ClearCache();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { success = true, message = "Cache cleared" }, ct);
        }
    }
}
