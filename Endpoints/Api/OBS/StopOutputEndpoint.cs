using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.OBS
{
    public class OutputNameRequest
    {
        public string OutputName { get; set; } = string.Empty;
    }

    public class StopOutputEndpoint : Endpoint<OutputNameRequest>
    {
        private readonly IOBSService _obs;
        private readonly ILogger<StopOutputEndpoint> _logger;

        public StopOutputEndpoint(IOBSService obs, ILogger<StopOutputEndpoint> logger)
        {
            _obs = obs;
            _logger = logger;
        }

        public override void Configure()
        {
            Post("/api/obs/stop/{outputName}");
            AllowAnonymous();
        }

        public override async Task HandleAsync(OutputNameRequest req, CancellationToken ct)
        {
            try
            {
                await _obs.StopStreamAsync(req.OutputName, ct);
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Stopping output {OutputName} failed", req.OutputName);
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
