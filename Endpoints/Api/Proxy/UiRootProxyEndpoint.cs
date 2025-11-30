using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Endpoints.Api.Proxy
{
    public class UiRootProxyEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            // Accept all common HTTP methods so the UI works under a prefix
            Get("/mainsail/{**path}");
            Post("/mainsail/{**path}");
            Put("/mainsail/{**path}");
            Patch("/mainsail/{**path}");
            Delete("/mainsail/{**path}");
            Head("/mainsail/{**path}");
            Options("/mainsail/{**path}");

            Get("/fluidd/{**path}");
            Post("/fluidd/{**path}");
            Put("/fluidd/{**path}");
            Patch("/fluidd/{**path}");
            Delete("/fluidd/{**path}");
            Head("/fluidd/{**path}");
            Options("/fluidd/{**path}");

            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var cfg = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<UiRootProxyEndpoint>>();
            var path = Route<string?>("path");
            var route = HttpContext.Request.Path.Value ?? string.Empty;
            string? target = null;
            if (route.StartsWith("/mainsail", System.StringComparison.OrdinalIgnoreCase)) target = cfg.GetValue<string>("PrinterUI:MainsailUrl");
            else if (route.StartsWith("/fluidd", System.StringComparison.OrdinalIgnoreCase)) target = cfg.GetValue<string>("PrinterUI:FluiddUrl");
            logger.LogDebug("{Method} {Route} -> target={Target}", HttpContext.Request.Method, route, target ?? "NOT CONFIGURED");
            if (string.IsNullOrWhiteSpace(target))
            {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.Response.WriteAsync("UI target not configured", ct);
                return;
            }
            await ProxyUtil.ProxyRequest(HttpContext, target, path ?? string.Empty);
        }
    }
}
