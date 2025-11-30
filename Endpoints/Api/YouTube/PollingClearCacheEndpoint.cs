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
        public override void Configure() { Post("/api/youtube/polling/clear-cache"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var pm = HttpContext.RequestServices.GetRequiredService<YouTubePollingManager>();
            pm.ClearCache();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { success = true, message = "Cache cleared" }, ct);
        }
    }
}
