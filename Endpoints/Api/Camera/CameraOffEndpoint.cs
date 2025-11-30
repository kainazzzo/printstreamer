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
        private readonly WebCamManager _webcamManager;
        private readonly StreamService _streamService;
        private readonly ILogger<CameraOffEndpoint> _logger;

        public CameraOffEndpoint(WebCamManager webcamManager, StreamService streamService, ILogger<CameraOffEndpoint> logger)
        {
            _webcamManager = webcamManager;
            _streamService = streamService;
            _logger = logger;
        }
        public override void Configure()
        {
            Post("/api/camera/off");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            _webcamManager.SetDisabled(true);
            _logger.LogInformation("Camera simulation: disabled (camera off)");
            if (_streamService.IsStreaming)
            {
                try
                {
                    await _streamService.StopStreamAsync();
                    await _streamService.StartStreamAsync(null, null, ct);
                }
                catch (System.Exception ex)
                {
                    _logger.LogError(ex, "Failed to restart stream");
                }
            }
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { disabled = _webcamManager.IsDisabled }, ct);
        }
    }
}
