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
        public override void Configure()
        {
            Get("/stream/overlay");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                var cfg = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                var overlayText = HttpContext.RequestServices.GetRequiredService<OverlayTextService>();
                var logger = HttpContext.RequestServices.GetRequiredService<ILogger<OverlayMjpegStreamer>>();
                var streamer = new OverlayMjpegStreamer(cfg, overlayText, HttpContext, logger);
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
