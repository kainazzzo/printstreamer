using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;
using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Endpoints.Api.Overlay
{
    public class OverlayEnabledSetEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly OverlayProcessService _overlayProcessService;
        private readonly ILogger<OverlayEnabledSetEndpoint> _logger;

        public OverlayEnabledSetEndpoint(IConfiguration config, OverlayProcessService overlayProcessService, ILogger<OverlayEnabledSetEndpoint> logger)
        {
            _config = config;
            _overlayProcessService = overlayProcessService;
            _logger = logger;
        }

        public override void Configure() { Post("/api/overlay/enabled"); AllowAnonymous(); }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            try
            {
                var enabledStr = ctx.Request.Query["enabled"].ToString();
                var enabled = string.Equals(enabledStr, "true", System.StringComparison.OrdinalIgnoreCase) || enabledStr == "1";
                _config["Overlay:Enabled"] = enabled.ToString();
                _logger.LogInformation("Overlay: {State}", enabled ? "enabled" : "disabled");

                if (!enabled)
                {
                    // Overlay is being disabled - terminate all overlay ffmpeg processes
                    _logger.LogInformation("Terminating overlay ffmpeg process(es)");
                    _overlayProcessService.TerminateAllProcesses();
                }

                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to toggle overlay");
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
