using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.OBS
{
    public class StopEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IOBSService _obs;
        private readonly ILogger<StopEndpoint> _logger;

        public StopEndpoint(IOBSService obs, ILogger<StopEndpoint> logger)
        {
            _obs = obs;
            _logger = logger;
        }

        public override void Configure()
        {
            Post("/api/obs/stop");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                await _obs.StopCurrentStreamAsync(ct);
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Stopping current stream failed");
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
