using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;

namespace PrintStreamer.Endpoints.Stream
{
    public class CaptureOverlayEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Get("/stream/overlay/capture");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            await CaptureJpegHelper.TryCaptureJpegFromStreamAsync(HttpContext, "http://127.0.0.1:8080/stream/overlay", "overlay stream", ct);
        }
    }
}
