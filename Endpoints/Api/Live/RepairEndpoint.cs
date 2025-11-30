using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Live
{
    public class RepairEndpoint : EndpointWithoutRequest<object>
    {
        private readonly StreamOrchestrator _orchestrator;

        public RepairEndpoint(StreamOrchestrator orchestrator)
        {
            _orchestrator = orchestrator;
        }

        public override void Configure()
        {
            Post("/api/live/repair");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                var ok = await _orchestrator.EnsureStreamingHealthyAsync(ct);
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { success = ok }, ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
