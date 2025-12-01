using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Streamers;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Stream
{
    public class MixEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly ILogger<MixStreamer> _mixStreamerLogger;
        private readonly MixStreamHostedService _mixHostedService;

        public MixEndpoint(IConfiguration config, ILogger<MixStreamer> mixStreamerLogger, MixStreamHostedService mixHostedService)
        {
            _config = config;
            _mixStreamerLogger = mixStreamerLogger;
            _mixHostedService = mixHostedService;
        }
        public override void Configure()
        {
            Get("/stream/mix");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                // Check if mix processing is enabled
                var mixEnabled = _config.GetValue<bool?>("Stream:Mix:Enabled") ?? true;
                if (!mixEnabled)
                {
                    HttpContext.Response.StatusCode = 503; // Service Unavailable
                    await HttpContext.Response.WriteAsync("Mix processing is disabled (Stream:Mix:Enabled=false)", ct);
                    return;
                }

                var streamer = new MixStreamer(_config, HttpContext, _mixStreamerLogger);
                
                // After starting, register the process with the hosted service so it can be terminated if needed
                var startTask = streamer.StartAsync(ct);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(100, ct); // Give ffmpeg time to start
                        // The process is running now, but we need access to it
                        // This is a limitation of the current architecture
                    }
                    catch { }
                }, ct);
                
                await startTask;
            }
            catch (System.Exception ex)
            {
                if (!HttpContext.Response.HasStarted)
                {
                    HttpContext.Response.StatusCode = 500;
                    await HttpContext.Response.WriteAsync("Mix streamer error: " + ex.Message);
                }
            }
        }
    }
}
