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
        private readonly IConfiguration _config;

        public CaptureSourceEndpoint(IConfiguration config)
        {
            _config = config;
        }
        public override void Configure()
        {
            Get("/stream/source/capture");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var source = _config.GetValue<string>("Stream:Source") ?? string.Empty;
            await CaptureJpegHelper.TryCaptureJpegFromStreamAsync(HttpContext, source, "stream source", ct);
        }
    }
}
