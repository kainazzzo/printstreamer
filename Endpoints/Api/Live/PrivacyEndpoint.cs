using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Live
{
    public class PrivacyRequest { public string? Privacy { get; set; } }

    public class PrivacyEndpoint : Endpoint<PrivacyRequest>
    {
        public override void Configure()
        {
            Post("/api/live/privacy");
            AllowAnonymous();
        }

        public override async Task HandleAsync(PrivacyRequest req, CancellationToken ct)
        {
            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<PrivacyEndpoint>>();
            try
            {
                var orchestrator = HttpContext.RequestServices.GetRequiredService<StreamOrchestrator>();
                if (!orchestrator.IsBroadcastActive || string.IsNullOrWhiteSpace(orchestrator.CurrentBroadcastId))
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "No active broadcast" }, ct);
                    return;
                }
                if (req == null || string.IsNullOrWhiteSpace(req.Privacy))
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Privacy status is required" }, ct);
                    return;
                }

                var broadcastId = orchestrator.CurrentBroadcastId!;
                var yt = HttpContext.RequestServices.GetRequiredService<YouTubeControlService>();
                logger.LogInformation("HTTP /api/live/privacy request received: {Privacy}", req.Privacy);
                if (!await yt.AuthenticateAsync(ct))
                {
                    HttpContext.Response.StatusCode = 401;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "YouTube authentication failed" }, ct);
                    return;
                }

                var ok = await yt.UpdateBroadcastPrivacyAsync(broadcastId, req.Privacy!, ct);
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
