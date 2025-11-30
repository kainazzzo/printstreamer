using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;

namespace PrintStreamer.Endpoints.Config
{
    public class GetConfigStateEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Get("/api/config/state");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var config = HttpContext.RequestServices.GetService(typeof(IConfiguration)) as IConfiguration;

            var autoBroadcastEnabled = config?.GetValue<bool>("YouTube:LiveBroadcast:Enabled");
            var autoUploadEnabled = config?.GetValue<bool>("YouTube:TimelapseUpload:Enabled");
            var endStreamAfterPrintEnabled = config?.GetValue<bool?>("YouTube:LiveBroadcast:EndStreamAfterPrint") ?? false;
            var audioEnabled = config?.GetValue<bool?>("Audio:Enabled") ?? true;

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
