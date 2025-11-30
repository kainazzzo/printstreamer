using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Stream
{
    public class StreamProxyEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Get("/stream");
            Get("/stream/source");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var webcamManager = HttpContext.RequestServices.GetRequiredService<WebCamManager>();
            await webcamManager.HandleStreamRequest(HttpContext);
        }
    }
}
