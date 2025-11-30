using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Stream
{
    public class EndAfterSongSetEndpoint : EndpointWithoutRequest<object>
    {
        private readonly StreamOrchestrator _orchestrator;

        public EndAfterSongSetEndpoint(StreamOrchestrator orchestrator)
        {
            _orchestrator = orchestrator;
        }

        public override void Configure() { Post("/api/stream/end-after-song"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            try
            {
                var enabledStr = ctx.Request.Query["enabled"].ToString();
                var enabled = string.Equals(enabledStr, "true", System.StringComparison.OrdinalIgnoreCase) || enabledStr == "1";
                _orchestrator.SetEndStreamAfterSong(enabled);
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
            }
            catch (System.Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
