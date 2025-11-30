using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Live
{
    public class DebugEndpoint : EndpointWithoutRequest<object>
    {
        private readonly ILogger<DebugEndpoint> _logger;
        private readonly StreamOrchestrator _orchestrator;
        private readonly YouTubeControlService _yt;

        public DebugEndpoint(ILogger<DebugEndpoint> logger, StreamOrchestrator orchestrator, YouTubeControlService yt)
        {
            _logger = logger;
            _orchestrator = orchestrator;
            _yt = yt;
        }

        public override void Configure()
        {
            Get("/api/live/debug");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                if (!_orchestrator.IsBroadcastActive || string.IsNullOrWhiteSpace(_orchestrator.CurrentBroadcastId))
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "No active broadcast" }, ct);
                    return;
                }
                var bid = _orchestrator.CurrentBroadcastId!;
                _logger.LogInformation("HTTP /api/live/debug request received");
                if (!await _yt.AuthenticateAsync(ct))
                {
                    HttpContext.Response.StatusCode = 401;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "YouTube authentication failed" }, ct);
                    return;
                }
                await _yt.LogBroadcastAndStreamResourcesAsync(bid, null, ct);
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
