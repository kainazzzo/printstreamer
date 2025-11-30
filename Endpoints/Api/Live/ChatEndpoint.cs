using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Live
{
    public class ChatMessageRequest { public string Message { get; set; } = string.Empty; }

    public class ChatEndpoint : Endpoint<ChatMessageRequest>
    {
        public override void Configure()
        {
            Post("/api/live/chat");
            AllowAnonymous();
        }

        public override async Task HandleAsync(ChatMessageRequest req, CancellationToken ct)
        {
            try
            {
                var orchestrator = HttpContext.RequestServices.GetRequiredService<StreamOrchestrator>();
                if (!orchestrator.IsBroadcastActive || string.IsNullOrWhiteSpace(orchestrator.CurrentBroadcastId))
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "No active broadcast" }, ct);
                    return;
                }

                if (req == null || string.IsNullOrWhiteSpace(req.Message))
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Message is required" }, ct);
                    return;
                }

                var yt = HttpContext.RequestServices.GetRequiredService<YouTubeControlService>();
                var ok = await yt.SendChatMessageAsync(orchestrator.CurrentBroadcastId!, req.Message, ct);
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
