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
        private readonly ILogger<ChatEndpoint> _logger;
        private readonly StreamOrchestrator _orchestrator;
        private readonly YouTubeControlService _yt;

        public ChatEndpoint(ILogger<ChatEndpoint> logger, StreamOrchestrator orchestrator, YouTubeControlService yt)
        {
            _logger = logger;
            _orchestrator = orchestrator;
            _yt = yt;
        }

        public override void Configure()
        {
            Post("/api/live/chat");
            AllowAnonymous();
        }

        public override async Task HandleAsync(ChatMessageRequest req, CancellationToken ct)
        {
            try
            {
                if (!_orchestrator.IsBroadcastActive || string.IsNullOrWhiteSpace(_orchestrator.CurrentBroadcastId))
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

                var ok = await _yt.SendChatMessageAsync(_orchestrator.CurrentBroadcastId!, req.Message, ct);
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
