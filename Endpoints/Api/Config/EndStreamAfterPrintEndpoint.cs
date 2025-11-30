using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System.Threading;

namespace PrintStreamer.Endpoints.Api.Config
{
    public class EndStreamAfterPrintEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly ILogger<EndStreamAfterPrintEndpoint> _logger;

        public EndStreamAfterPrintEndpoint(IConfiguration config, ILogger<EndStreamAfterPrintEndpoint> logger)
        {
            _config = config;
            _logger = logger;
        }

        public override void Configure() { Post("/api/config/end-stream-after-print"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            var raw = ctx.Request.Query["enabled"].ToString();
            bool enabled;
            if (!bool.TryParse(raw, out enabled)) enabled = raw == "1";
            _config["YouTube:LiveBroadcast:EndStreamAfterPrint"] = enabled.ToString();
            _logger.LogInformation("End stream after print: {State}", enabled ? "enabled" : "disabled");
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
        }
    }
}
