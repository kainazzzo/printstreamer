using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Endpoints.Api.Proxy
{
    public class ProxyFluiddEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Get("/proxy/fluidd/{**path}");
            Get("/proxy/fluidd");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var cfg = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<ProxyFluiddEndpoint>>();
            var target = cfg.GetValue<string>("PrinterUI:FluiddUrl");
            var path = Route<string?>("path");
            logger.LogDebug("GET /proxy/fluidd/{Path} -> target={Target}", path ?? "", target ?? "NOT CONFIGURED");
            if (string.IsNullOrWhiteSpace(target))
            {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.Response.WriteAsync("Fluidd URL not configured", ct);
                return;
            }
            await ProxyUtil.ProxyRequest(HttpContext, target, path ?? "");
        }
    }
}
