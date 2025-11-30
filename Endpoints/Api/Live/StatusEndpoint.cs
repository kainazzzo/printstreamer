using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Live
{
    public class StatusEndpoint : EndpointWithoutRequest<object>
    {
        private readonly ILogger<StatusEndpoint> _logger;
        private readonly IStreamOrchestrator _orchestrator;
        private readonly YouTubeControlService _ytService;

        public StatusEndpoint(ILogger<StatusEndpoint> logger, IStreamOrchestrator orchestrator, YouTubeControlService ytService)
        {
            _logger = logger;
            _orchestrator = orchestrator;
            _ytService = ytService;
        }

        public override void Configure()
        {
            Get("/api/live/status");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                var isLive = _orchestrator.IsBroadcastActive;
                var broadcastId = _orchestrator.CurrentBroadcastId;
                var streamerRunning = _orchestrator.IsStreaming;
                var waitingForIngestion = _orchestrator.IsWaitingForIngestion;
                string? privacy = null;

                if (isLive && !string.IsNullOrWhiteSpace(broadcastId))
                {
                    try
                    {
                        _logger.LogInformation("HTTP /api/live/status request received");
                        if (await _ytService.AuthenticateAsync(ct))
                        {
                            privacy = await _ytService.GetBroadcastPrivacyAsync(broadcastId, ct);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { isLive, broadcastId, streamerRunning, waitingForIngestion, privacy }, ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { isLive = false, broadcastId = (string?)null, streamerRunning = false, waitingForIngestion = false, privacy = (string?)null, error = ex.Message }, ct);
            }
        }
    }
}
