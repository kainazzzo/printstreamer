using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Camera
{
    public class CameraOffEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Post("/api/camera/off");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var webcamManager = HttpContext.RequestServices.GetRequiredService<WebCamManager>();
            var streamService = HttpContext.RequestServices.GetRequiredService<StreamService>();
            webcamManager.SetDisabled(true);
            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<CameraOffEndpoint>>();
            logger.LogInformation("Camera simulation: disabled (camera off)");
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
            await HttpContext.Response.WriteAsJsonAsync(new { disabled = webcamManager.IsDisabled }, ct);
        }
    }
}
