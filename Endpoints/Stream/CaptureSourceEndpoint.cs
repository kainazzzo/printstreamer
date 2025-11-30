using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace PrintStreamer.Endpoints.Stream
{
    public class CaptureSourceEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Get("/stream/source/capture");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var source = config.GetValue<string>("Stream:Source") ?? string.Empty;
            await CaptureJpegHelper.TryCaptureJpegFromStreamAsync(HttpContext, source, "stream source", ct);
        }
    }
}
