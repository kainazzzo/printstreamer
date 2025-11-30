using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Streamers;
using PrintStreamer.Overlay;

namespace PrintStreamer.Endpoints.Stream
{
    public class OverlayEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly Overlay.OverlayTextService _overlayText;
        private readonly ILogger<OverlayMjpegStreamer> _overlayStreamerLogger;

        public OverlayEndpoint(IConfiguration config, Overlay.OverlayTextService overlayText, ILogger<OverlayMjpegStreamer> overlayStreamerLogger)
        {
            _config = config;
            _overlayText = overlayText;
            _overlayStreamerLogger = overlayStreamerLogger;
        }
        public override void Configure()
        {
            Get("/stream/overlay");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                var streamer = new OverlayMjpegStreamer(_config, _overlayText, HttpContext, _overlayStreamerLogger);
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
