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
        public override void Configure()
        {
            Get("/stream/overlay/coords");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var overlayText = HttpContext.RequestServices.GetRequiredService<OverlayTextService>();
            var fontSize = config.GetValue<int?>("Overlay:FontSize") ?? 16;
            var boxHeight = config.GetValue<int?>("Overlay:BoxHeight") ?? 75;
            var layout = OverlayLayout.Calculate(config, overlayText.TextFilePath, fontSize, boxHeight);
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
