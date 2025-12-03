using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Stream
{
    public class AudioEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly ILogger<AudioEndpoint> _logger;
        private readonly AudioBroadcastService _broadcaster;

        public AudioEndpoint(IConfiguration config, ILogger<AudioEndpoint> logger, AudioBroadcastService broadcaster)
        {
            _config = config;
            _logger = logger;
            _broadcaster = broadcaster;
        }
        public override void Configure()
        {
            Get("/api/audio/stream", "/stream/audio");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var enabled = _config.GetValue<bool?>("Audio:Enabled") ?? true;
            if (!enabled)
            {
                // Audio is disabled - return 503 Service Unavailable (like mix endpoint)
                HttpContext.Response.StatusCode = 503;
                await HttpContext.Response.WriteAsync("Audio stream is disabled", ct);
                return;
            }

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.Headers["Content-Type"] = "audio/mpeg";
            HttpContext.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            HttpContext.Response.Headers["Pragma"] = "no-cache";
            await HttpContext.Response.Body.FlushAsync(ct);

            try
            {
                await foreach (var chunk in _broadcaster.Stream(ct))
                {
                    await HttpContext.Response.Body.WriteAsync(chunk, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Client stream error");
            }
        }
    }
}
