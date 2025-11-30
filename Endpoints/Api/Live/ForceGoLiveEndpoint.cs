using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;
using System;

namespace PrintStreamer.Endpoints.Api.Live
{
    public class ForceGoLiveEndpoint : EndpointWithoutRequest<object>
    {
        private readonly ILogger<ForceGoLiveEndpoint> _logger;
        private readonly IStreamOrchestrator _orchestrator;
        private readonly YouTubeControlService _ytService;

        public ForceGoLiveEndpoint(ILogger<ForceGoLiveEndpoint> logger, IStreamOrchestrator orchestrator, YouTubeControlService ytService)
        {
            _logger = logger;
            _orchestrator = orchestrator;
            _ytService = ytService;
        }

        public override void Configure()
        {
            Post("/api/live/force-go-live");
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
                _logger.LogInformation("HTTP /api/live/force-go-live request received");
                if (!await _ytService.AuthenticateAsync(ct))
                {
                    HttpContext.Response.StatusCode = 401;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "YouTube authentication failed" }, ct);
                    return;
                }
                var ok = await _ytService.TransitionBroadcastToLiveWhenReadyAsync(bid, TimeSpan.FromSeconds(180), 12, ct);
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
