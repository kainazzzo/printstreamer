using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Camera
{
    public class CameraToggleEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Post("/api/camera/toggle");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var webcamManager = HttpContext.RequestServices.GetRequiredService<WebCamManager>();
            var streamService = HttpContext.RequestServices.GetRequiredService<StreamService>();
            webcamManager.Toggle();
            var newVal = webcamManager.IsDisabled;
            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<CameraToggleEndpoint>>();
            logger.LogInformation("Camera simulation: toggled -> disabled={IsDisabled}", newVal);
            if (streamService.IsStreaming)
            {
                try
                {
                    await streamService.StopStreamAsync();
                    await streamService.StartStreamAsync(null, null, ct);
                }
                catch (System.Exception ex)
                {
                    logger.LogError(ex, "Failed to restart stream");
                }
            }
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { disabled = newVal }, ct);
        }
    }
}
