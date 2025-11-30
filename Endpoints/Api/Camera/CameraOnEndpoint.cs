using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Camera
{
    public class CameraOnEndpoint : EndpointWithoutRequest<object>
    {
        private readonly WebCamManager _webcamManager;
        private readonly StreamService _streamService;
        private readonly ILogger<CameraOnEndpoint> _logger;

        public CameraOnEndpoint(WebCamManager webcamManager, StreamService streamService, ILogger<CameraOnEndpoint> logger)
        {
            _webcamManager = webcamManager;
            _streamService = streamService;
            _logger = logger;
        }

        public override void Configure()
        {
            Post("/api/camera/on");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            _webcamManager.SetDisabled(false);
            _logger.LogInformation("Camera simulation: enabled (camera on)");
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
