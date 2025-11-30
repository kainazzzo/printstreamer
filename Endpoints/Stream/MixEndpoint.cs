using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrintStreamer.Streamers;

namespace PrintStreamer.Endpoints.Stream
{
    public class MixEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly ILoggerFactory _loggerFactory;

        public MixEndpoint(IConfiguration config, ILoggerFactory loggerFactory)
        {
            _config = config;
            _loggerFactory = loggerFactory;
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
                var streamer = new MixStreamer(_config, HttpContext, _loggerFactory.CreateLogger<MixStreamer>());
                await streamer.StartAsync(ct);
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
