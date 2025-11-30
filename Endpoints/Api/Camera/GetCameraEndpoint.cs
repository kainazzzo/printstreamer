using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Camera
{
    public class GetCameraEndpoint : EndpointWithoutRequest<object>
    {
        private readonly WebCamManager _webcamManager;

        public GetCameraEndpoint(WebCamManager webcamManager)
        {
            _webcamManager = webcamManager;
        }
        
        public override void Configure()
        {
            Get("/api/camera");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { disabled = _webcamManager.IsDisabled }, ct);
        }
    }
}
