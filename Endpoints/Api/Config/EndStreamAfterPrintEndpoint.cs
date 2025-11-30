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
        public override void Configure() { Post("/api/config/end-stream-after-print"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            var raw = ctx.Request.Query["enabled"].ToString();
            bool enabled;
            if (!bool.TryParse(raw, out enabled)) enabled = raw == "1";
            var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
            config["YouTube:LiveBroadcast:EndStreamAfterPrint"] = enabled.ToString();
            var logger = ctx.RequestServices.GetRequiredService<ILogger<EndStreamAfterPrintEndpoint>>();
            logger.LogInformation("End stream after print: {State}", enabled ? "enabled" : "disabled");
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
        }
    }
}
