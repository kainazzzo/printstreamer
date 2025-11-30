using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Endpoints.Api.Proxy
{
    public class ProxyMainsailEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<ProxyMainsailEndpoint> _logger;

        public ProxyMainsailEndpoint(IConfiguration cfg, ILogger<ProxyMainsailEndpoint> logger)
        {
            _cfg = cfg;
            _logger = logger;
        }

        public override void Configure()
        {
            Get("/proxy/mainsail/{**path}", "/proxy/mainsail");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var target = _cfg.GetValue<string>("PrinterUI:MainsailUrl");
            var path = Route<string?>("path");
            _logger.LogDebug("GET /proxy/mainsail/{Path} -> target={Target}", path ?? "", target ?? "NOT CONFIGURED");
            if (string.IsNullOrWhiteSpace(target))
            {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.Response.WriteAsync("Mainsail URL not configured", ct);
                return;
            }
            await ProxyUtil.ProxyRequest(HttpContext, target, path ?? "");
        }
    }
}
