using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;

namespace PrintStreamer.Endpoints.Config
{
    public class GetConfigStateEndpoint : EndpointWithoutRequest<object>
    {
        private readonly ILogger<GetConfigStateEndpoint> _logger;
        private readonly IConfiguration _config;

        public GetConfigStateEndpoint(ILogger<GetConfigStateEndpoint> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        public override void Configure()
        {
            Get("/api/config/state");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var autoBroadcastEnabled = _config.GetValue<bool>("YouTube:LiveBroadcast:Enabled");
            var autoUploadEnabled = _config.GetValue<bool>("YouTube:TimelapseUpload:Enabled");
            var endStreamAfterPrintEnabled = _config.GetValue<bool?>("YouTube:LiveBroadcast:EndStreamAfterPrint") ?? false;
            var audioEnabled = _config.GetValue<bool?>("Audio:Enabled") ?? true;
            var result = new {
                autoBroadcastEnabled,
                autoUploadEnabled,
                endStreamAfterPrintEnabled,
                audioEnabled
            };

            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(result, ct);
        }
    }
}
