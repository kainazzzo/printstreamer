using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Overlay;
using PrintStreamer.Streamers;

namespace PrintStreamer.Endpoints.Stream
{
    public class OverlayCoordsEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly Overlay.OverlayTextService _overlayText;

        public OverlayCoordsEndpoint(IConfiguration config, Overlay.OverlayTextService overlayText)
        {
            _config = config;
            _overlayText = overlayText;
        }
        public override void Configure()
        {
            Get("/stream/overlay/coords");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var fontSize = _config.GetValue<int?>("Overlay:FontSize") ?? 16;
            var boxHeight = _config.GetValue<int?>("Overlay:BoxHeight") ?? 75;
            var layout = OverlayLayout.Calculate(_config, _overlayText.TextFilePath, fontSize, boxHeight);
            var result = new
            {
                drawbox = new { x = layout.DrawboxX, y = layout.DrawboxY },
                text = new { x = layout.TextX, y = layout.TextY },
                layout.HasCustomX,
                layout.HasCustomY,
                layout.ApproxTextHeight,
                raw = new { x = layout.RawX, y = layout.RawY }
            };
            await HttpContext.Response.WriteAsJsonAsync(result, ct);
        }
    }
}
