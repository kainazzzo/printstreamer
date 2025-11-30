using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class AudioEnabledEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Post("/api/audio/enabled"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            var logger = ctx.RequestServices.GetRequiredService<ILogger<AudioEnabledEndpoint>>();
            var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
            var raw = ctx.Request.Query["enabled"].ToString();
            bool enabled;
            if (!bool.TryParse(raw, out enabled)) enabled = raw == "1" || string.Equals(raw, "true", System.StringComparison.OrdinalIgnoreCase);
            config["Audio:Enabled"] = enabled.ToString();
            logger.LogInformation("Audio stream: {State}", enabled ? "enabled" : "disabled");
            try
            {
                var streamService = ctx.RequestServices.GetRequiredService<StreamService>();
                if (streamService.IsStreaming)
                {
                    logger.LogInformation("Restarting active stream to pick up audio setting change");
                    await streamService.StopStreamAsync();
                    await streamService.StartStreamAsync(null, null, ct);
                }
            }
            catch (System.Exception ex) { logger.LogError(ex, "Failed to restart stream after audio toggle"); }

            try
            {
                var broadcaster = ctx.RequestServices.GetService<AudioBroadcastService>();
                broadcaster?.ApplyAudioEnabledState(enabled);
            }
            catch (System.Exception ex) { logger.LogError(ex, "Failed to apply audio toggle to broadcaster"); }

            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
        }
    }
}
