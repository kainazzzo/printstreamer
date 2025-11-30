using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.OBS
{
    public class ConnectEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IOBSService _obs;
        private readonly ILogger<ConnectEndpoint> _logger;

        public ConnectEndpoint(IOBSService obs, ILogger<ConnectEndpoint> logger)
        {
            _obs = obs;
            _logger = logger;
        }

        public override void Configure()
        {
            Post("/api/obs/connect");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                await _obs.ConnectAsync(ct);
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "OBS connect failed");
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
