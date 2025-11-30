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
        public override void Configure() { Post("/api/config/auto-upload"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            var raw = ctx.Request.Query["enabled"].ToString();
            bool enabled;
            if (!bool.TryParse(raw, out enabled)) enabled = raw == "1";
            var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
            config["YouTube:TimelapseUpload:Enabled"] = enabled.ToString();
            var logger = ctx.RequestServices.GetRequiredService<ILogger<AutoUploadEndpoint>>();
            logger.LogInformation("Auto-upload timelapses: {State}", enabled ? "enabled" : "disabled");
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
        }
    }
}
