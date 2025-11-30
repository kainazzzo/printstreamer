using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System.Threading;

namespace PrintStreamer.Endpoints.Api.Config
{
    public class AutoUploadEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly ILogger<AutoUploadEndpoint> _logger;

        public AutoUploadEndpoint(IConfiguration config, ILogger<AutoUploadEndpoint> logger)
        {
            _config = config;
            _logger = logger;
        }

        public override void Configure() { Post("/api/config/auto-upload"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            var raw = ctx.Request.Query["enabled"].ToString();
            bool enabled;
            if (!bool.TryParse(raw, out enabled)) enabled = raw == "1";
            _config["YouTube:TimelapseUpload:Enabled"] = enabled.ToString();
            _logger.LogInformation("Auto-upload timelapses: {State}", enabled ? "enabled" : "disabled");
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
        }
    }
}
