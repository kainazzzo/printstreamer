using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Stream
{
    public class AudioEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Get("/stream/audio");
            Get("/api/audio/stream");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var cfg = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var enabled = cfg.GetValue<bool?>("Audio:Enabled") ?? true;
            if (!enabled)
            {
                var logger = HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                await StreamHelpers.StreamSilentAudioAsync(HttpContext, logger, ct);
                return;
            }

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.Headers["Content-Type"] = "audio/mpeg";
            HttpContext.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            HttpContext.Response.Headers["Pragma"] = "no-cache";
            await HttpContext.Response.Body.FlushAsync(ct);

            var broadcaster = HttpContext.RequestServices.GetRequiredService<AudioBroadcastService>();
            try
            {
                await foreach (var chunk in broadcaster.Stream(ct))
                {
                    await HttpContext.Response.Body.WriteAsync(chunk, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (System.Exception ex)
            {
                var logger = HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "Client stream error");
            }
        }
    }
}
