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
        public override void Configure()
        {
            Get("/api/live/status");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<StatusEndpoint>>();
            try
            {
                var orchestrator = HttpContext.RequestServices.GetRequiredService<StreamOrchestrator>();
                var isLive = orchestrator.IsBroadcastActive;
                var broadcastId = orchestrator.CurrentBroadcastId;
                var streamerRunning = orchestrator.IsStreaming;
                var waitingForIngestion = orchestrator.IsWaitingForIngestion;
                string? privacy = null;

                if (isLive && !string.IsNullOrWhiteSpace(broadcastId))
                {
                    try
                    {
                        var yt = HttpContext.RequestServices.GetRequiredService<YouTubeControlService>();
                        logger.LogInformation("HTTP /api/live/status request received");
                        if (await yt.AuthenticateAsync(ct))
                        {
                            privacy = await yt.GetBroadcastPrivacyAsync(broadcastId, ct);
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
