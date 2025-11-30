using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;
using System.Linq;

namespace PrintStreamer.Endpoints.Api.OBS
{
    public class OutputsEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IOBSService _obs;
        private readonly ILogger<OutputsEndpoint> _logger;

        public OutputsEndpoint(IOBSService obs, ILogger<OutputsEndpoint> logger)
        {
            _obs = obs;
            _logger = logger;
        }

        public override void Configure()
        {
            Get("/api/obs/outputs");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                var outputs = await _obs.GetActiveOutputsAsync(ct);
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(outputs.Select(o => new
                {
                    name = o.Name,
                    type = o.Type.ToString(),
                    active = o.Active,
                    durationMs = o.Duration?.TotalMilliseconds,
                    bytes = o.Bytes
                }), ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
