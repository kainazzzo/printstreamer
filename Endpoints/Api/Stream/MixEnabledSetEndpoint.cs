using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using PrintStreamer.Services;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Endpoints.Api.Stream
{
    public class MixEnabledSetEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly StreamService _streamService;
        private readonly ILogger<MixEnabledSetEndpoint> _logger;

        public MixEnabledSetEndpoint(IConfiguration config, StreamService streamService, ILogger<MixEnabledSetEndpoint> logger)
        {
            _config = config;
            _streamService = streamService;
            _logger = logger;
        }

        public override void Configure() { Post("/api/stream/mix-enabled"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            try
            {
                var enabledStr = ctx.Request.Query["enabled"].ToString();
                var enabled = string.Equals(enabledStr, "true", System.StringComparison.OrdinalIgnoreCase) || enabledStr == "1";
                _config["Stream:Mix:Enabled"] = enabled.ToString();
                _logger.LogInformation("Mix processing (Stream:Mix:Enabled): {State}", enabled ? "enabled" : "disabled");
                
                // Handle stream restart/stop based on mix enabled state
                try
                {
                    if (_streamService.IsStreaming)
                    {
                        if (enabled)
                        {
                            // Mix is being enabled - restart to pick up the new setting and use /stream/mix
                            _logger.LogInformation("Restarting active stream to use mix processing");
                            await _streamService.StopStreamAsync();
                            await _streamService.StartStreamAsync(null, ct);
                        }
                        else
                        {
                            // Mix is being disabled - TERMINATE the broadcast completely (no fallback)
                            _logger.LogInformation("Mix disabled - stopping stream completely");
                            await _streamService.StopStreamAsync();
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    _logger.LogError(ex, "Failed to handle stream state change after mix toggle");
                }

                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
            }
            catch (System.Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
