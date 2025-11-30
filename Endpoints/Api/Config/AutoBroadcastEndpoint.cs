using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System.Threading;

namespace PrintStreamer.Endpoints.Api.Config
{
    public class AutoBroadcastEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly ILogger<AutoBroadcastEndpoint> _logger;

        public AutoBroadcastEndpoint(IConfiguration config, ILogger<AutoBroadcastEndpoint> logger)
        {
            _config = config;
            _logger = logger;
        }

        public override void Configure()
        {
            Post("/api/config/auto-broadcast");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            var raw = ctx.Request.Query["enabled"].ToString();
            bool enabled;
            if (!bool.TryParse(raw, out enabled)) enabled = raw == "1";
            _config["YouTube:LiveBroadcast:Enabled"] = enabled.ToString();
            _logger.LogInformation("Auto-broadcast: {State}", enabled ? "enabled" : "disabled");
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
        }
    }
}
