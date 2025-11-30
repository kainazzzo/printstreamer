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
        private readonly WebCamManager _webcamManager;
        private readonly StreamService _streamService;
        private readonly ILogger<CameraToggleEndpoint> _logger;

        public CameraToggleEndpoint(WebCamManager webcamManager, StreamService streamService, ILogger<CameraToggleEndpoint> logger)
        {
            _webcamManager = webcamManager;
            _streamService = streamService;
            _logger = logger;
        }

        public override void Configure()
        {
            Post("/api/camera/toggle");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            _webcamManager.Toggle();
            var newVal = _webcamManager.IsDisabled;
            _logger.LogInformation("Camera simulation: toggled -> disabled={IsDisabled}", newVal);
            if (_streamService.IsStreaming)
            {
                try
                {
                    await _streamService.StopStreamAsync();
                    await _streamService.StartStreamAsync(null, ct);
                }
                catch (System.Exception ex)
                {
                    _logger.LogError(ex, "Failed to restart stream");
                }
            }
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { disabled = newVal }, ct);
        }
    }
}
