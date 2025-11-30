using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Live
{
    public class StopBroadcastEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IStreamOrchestrator _orchestrator;

        public StopBroadcastEndpoint(IStreamOrchestrator orchestrator)
        {
            _orchestrator = orchestrator;
        }

        public override void Configure()
        {
            Post("/api/live/stop");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                var (ok, message) = await _orchestrator.StopBroadcastAsync(ct);
                if (ok) {
                    HttpContext.Response.StatusCode = 200;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct);
                    return;
                }
                HttpContext.Response.StatusCode = 400;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = message }, ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
