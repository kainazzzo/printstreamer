using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Streamers;
using PrintStreamer.Overlay;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Stream
{
    public class OverlayEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly Overlay.OverlayTextService _overlayText;
        private readonly ILogger<OverlayMjpegStreamer> _overlayStreamerLogger;
        private readonly OverlayProcessService _overlayProcessService;

        public OverlayEndpoint(IConfiguration config, Overlay.OverlayTextService overlayText, ILogger<OverlayMjpegStreamer> overlayStreamerLogger, OverlayProcessService overlayProcessService)
        {
            _config = config;
            _overlayText = overlayText;
            _overlayStreamerLogger = overlayStreamerLogger;
            _overlayProcessService = overlayProcessService;
        }
        public override void Configure()
        {
            Get("/stream/overlay");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var enabled = _config.GetValue<bool?>("Overlay:Enabled") ?? true;
            if (!enabled)
            {
                // Overlay is disabled - return 503 Service Unavailable
                HttpContext.Response.StatusCode = 503;
                await HttpContext.Response.WriteAsync("Overlay stream is disabled", ct);
                return;
            }

            try
            {
                var streamer = new OverlayMjpegStreamer(_config, _overlayText, HttpContext, _overlayStreamerLogger, _overlayProcessService);
                await streamer.StartAsync(ct);
            }
            catch (System.Exception ex)
            {
                if (!HttpContext.Response.HasStarted)
                {
                    HttpContext.Response.StatusCode = 500;
                    await HttpContext.Response.WriteAsync("Overlay streamer error: " + ex.Message);
                }
            }
        }
    }
}
