using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Endpoints.Api.Proxy
{
    public class MoonrakerProxyEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<MoonrakerProxyEndpoint> _logger;

        public MoonrakerProxyEndpoint(IConfiguration cfg, ILogger<MoonrakerProxyEndpoint> logger)
        {
            _cfg = cfg;
            _logger = logger;
        }

        public override void Configure()
        {
            // All routes that need to support all HTTP methods
            var routes = new[] 
            { 
                "/fluidd/printer/{**path}", "/fluidd/api/{**path}", "/fluidd/server/{**path}", "/fluidd/machine/{**path}", "/fluidd/access/{**path}",
                "/mainsail/printer/{**path}", "/mainsail/api/{**path}", "/mainsail/server/{**path}", "/mainsail/machine/{**path}", "/mainsail/access/{**path}",
                "/printer/{**path}", "/server/{**path}", "/machine/{**path}", "/access/{**path}"
            };
            Get(routes);
            Post(routes);
            Put(routes);
            Patch(routes);
            Delete(routes);
            Head(routes);
            Options(routes);

            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var moonrakerBase = _cfg.GetValue<string>("Moonraker:BaseUrl");
            if (string.IsNullOrWhiteSpace(moonrakerBase))
            {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.Response.WriteAsync("Moonraker base URL not configured", ct);
                return;
            }

            var path = Route<string?>("path") ?? string.Empty;
            var reqPath = HttpContext.Request.Path.Value ?? string.Empty;
            string targetPathPrefix = "";

            if (reqPath.StartsWith("/fluidd/printer", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "printer/";
            else if (reqPath.StartsWith("/fluidd/api", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "api/";
            else if (reqPath.StartsWith("/fluidd/server", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "server/";
            else if (reqPath.StartsWith("/fluidd/machine", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "machine/";
            else if (reqPath.StartsWith("/fluidd/access", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "access/";
            else if (reqPath.StartsWith("/mainsail/printer", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "printer/";
            else if (reqPath.StartsWith("/mainsail/api", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "api/";
            else if (reqPath.StartsWith("/mainsail/server", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "server/";
            else if (reqPath.StartsWith("/mainsail/machine", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "machine/";
            else if (reqPath.StartsWith("/mainsail/access", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "access/";
            else if (reqPath.StartsWith("/printer", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "printer/";
            else if (reqPath.StartsWith("/server", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "server/";
            else if (reqPath.StartsWith("/machine", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "machine/";
            else if (reqPath.StartsWith("/access", System.StringComparison.OrdinalIgnoreCase)) targetPathPrefix = "access/";

            _logger.LogDebug("Proxy {Method} {ReqPath} -> moonraker={MoonrakerBase}, prefix={Prefix}", HttpContext.Request.Method, reqPath, moonrakerBase, targetPathPrefix);
            await ProxyUtil.ProxyRequest(HttpContext, moonrakerBase!, targetPathPrefix + path);
        }
    }
}
