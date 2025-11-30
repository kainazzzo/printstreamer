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
        public override void Configure()
        {
            Get("/api/camera");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var webcamManager = HttpContext.RequestServices.GetRequiredService<WebCamManager>();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { disabled = webcamManager.IsDisabled }, ct);
        }
    }
}
