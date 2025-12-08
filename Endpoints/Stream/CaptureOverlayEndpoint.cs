using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Stream
{
    public class CaptureOverlayEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly OverlayBroadcastService _overlayBroadcastService;

        public CaptureOverlayEndpoint(IConfiguration config, OverlayBroadcastService overlayBroadcastService)
        {
            _config = config;
            _overlayBroadcastService = overlayBroadcastService;
        }

        public override void Configure()
        {
            Get("/stream/overlay/capture");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var enabled = _config.GetValue<bool?>("Overlay:Enabled") ?? true;
            if (!enabled)
            {
                HttpContext.Response.StatusCode = 503;
                await HttpContext.Response.WriteAsync("Overlay stream is disabled", ct);
                return;
            }

            if (!await _overlayBroadcastService.EnsureRunningAsync(ct))
            {
                HttpContext.Response.StatusCode = 503;
                await HttpContext.Response.WriteAsync("Overlay stream unavailable", ct);
                return;
            }

            await CaptureJpegHelper.TryCaptureJpegFromStreamAsync(HttpContext, _overlayBroadcastService.OutputUrl, "overlay stream", ct);
        }
    }
}
