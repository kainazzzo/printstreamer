using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;

namespace PrintStreamer.Endpoints.Stream
{
    public class CaptureMixEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Get("/stream/mix/capture");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            await CaptureJpegHelper.TryCaptureJpegFromStreamAsync(HttpContext, "http://127.0.0.1:8080/stream/mix", "mix stream", ct);
        }
    }
}
