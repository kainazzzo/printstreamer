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
        public override void Configure()
        {
            Post("/api/live/force-go-live");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<ForceGoLiveEndpoint>>();
            try
            {
                var orchestrator = HttpContext.RequestServices.GetRequiredService<StreamOrchestrator>();
                if (!orchestrator.IsBroadcastActive || string.IsNullOrWhiteSpace(orchestrator.CurrentBroadcastId))
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "No active broadcast" }, ct);
                    return;
                }
                var bid = orchestrator.CurrentBroadcastId!;
                var yt = HttpContext.RequestServices.GetRequiredService<YouTubeControlService>();
                logger.LogInformation("HTTP /api/live/force-go-live request received");
                if (!await yt.AuthenticateAsync(ct))
                {
                    HttpContext.Response.StatusCode = 401;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "YouTube authentication failed" }, ct);
                    return;
                }
                var ok = await yt.TransitionBroadcastToLiveWhenReadyAsync(bid, TimeSpan.FromSeconds(180), 12, ct);
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
