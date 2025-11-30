using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Stream
{
    public class EndAfterSongGetEndpoint : EndpointWithoutRequest<object>
    {
        private readonly StreamOrchestrator _orchestrator;

        public EndAfterSongGetEndpoint(StreamOrchestrator orchestrator)
        {
            _orchestrator = orchestrator;
        }

        public override void Configure() { Get("/api/stream/end-after-song"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            try
            {
                var enabled = _orchestrator.IsEndStreamAfterSongEnabled;
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsJsonAsync(new { enabled }, ct);
            }
            catch (System.Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
