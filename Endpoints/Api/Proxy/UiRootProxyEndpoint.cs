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
        private readonly IConfiguration _cfg;
        private readonly ILogger<UiRootProxyEndpoint> _logger;

        public UiRootProxyEndpoint(IConfiguration cfg, ILogger<UiRootProxyEndpoint> logger)
        {
            _cfg = cfg;
            _logger = logger;
        }

        public override void Configure()
        {
            // Accept all common HTTP methods so the UI works under a prefix
            Get("/mainsail/{**path}", "/fluidd/{**path}");
            Post("/mainsail/{**path}", "/fluidd/{**path}");
            Put("/mainsail/{**path}", "/fluidd/{**path}");
            Patch("/mainsail/{**path}", "/fluidd/{**path}");
            Delete("/mainsail/{**path}", "/fluidd/{**path}");
            Head("/mainsail/{**path}", "/fluidd/{**path}");
            Options("/mainsail/{**path}", "/fluidd/{**path}");

            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var path = Route<string?>("path");
            var route = HttpContext.Request.Path.Value ?? string.Empty;
            string? target = null;
            if (route.StartsWith("/mainsail", System.StringComparison.OrdinalIgnoreCase)) target = _cfg.GetValue<string>("PrinterUI:MainsailUrl");
            else if (route.StartsWith("/fluidd", System.StringComparison.OrdinalIgnoreCase)) target = _cfg.GetValue<string>("PrinterUI:FluiddUrl");
            _logger.LogDebug("{Method} {Route} -> target={Target}", HttpContext.Request.Method, route, target ?? "NOT CONFIGURED");
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
