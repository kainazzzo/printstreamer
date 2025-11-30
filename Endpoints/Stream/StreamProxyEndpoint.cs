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
        private readonly WebCamManager _webcamManager;

        public StreamProxyEndpoint(WebCamManager webcamManager)
        {
            _webcamManager = webcamManager;
        }
        public override void Configure()
        {
            Get("/stream/source");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            await _webcamManager.HandleStreamRequest(HttpContext);
        }
    }
}
