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
        public override void Configure()
        {
            // fluidd prefixed routes
            Get("/fluidd/printer/{**path}"); Post("/fluidd/printer/{**path}"); Put("/fluidd/printer/{**path}"); Patch("/fluidd/printer/{**path}"); Delete("/fluidd/printer/{**path}"); Head("/fluidd/printer/{**path}"); Options("/fluidd/printer/{**path}");
            Get("/fluidd/api/{**path}"); Post("/fluidd/api/{**path}"); Put("/fluidd/api/{**path}"); Patch("/fluidd/api/{**path}"); Delete("/fluidd/api/{**path}"); Head("/fluidd/api/{**path}"); Options("/fluidd/api/{**path}");
            Get("/fluidd/server/{**path}"); Post("/fluidd/server/{**path}"); Put("/fluidd/server/{**path}"); Patch("/fluidd/server/{**path}"); Delete("/fluidd/server/{**path}"); Head("/fluidd/server/{**path}"); Options("/fluidd/server/{**path}");
            Get("/fluidd/machine/{**path}"); Post("/fluidd/machine/{**path}"); Put("/fluidd/machine/{**path}"); Patch("/fluidd/machine/{**path}"); Delete("/fluidd/machine/{**path}"); Head("/fluidd/machine/{**path}"); Options("/fluidd/machine/{**path}");
            Get("/fluidd/access/{**path}"); Post("/fluidd/access/{**path}"); Put("/fluidd/access/{**path}"); Patch("/fluidd/access/{**path}"); Delete("/fluidd/access/{**path}"); Head("/fluidd/access/{**path}"); Options("/fluidd/access/{**path}");

            // mainsail prefixed routes
            Get("/mainsail/printer/{**path}"); Post("/mainsail/printer/{**path}"); Put("/mainsail/printer/{**path}"); Patch("/mainsail/printer/{**path}"); Delete("/mainsail/printer/{**path}"); Head("/mainsail/printer/{**path}"); Options("/mainsail/printer/{**path}");
            Get("/mainsail/api/{**path}"); Post("/mainsail/api/{**path}"); Put("/mainsail/api/{**path}"); Patch("/mainsail/api/{**path}"); Delete("/mainsail/api/{**path}"); Head("/mainsail/api/{**path}"); Options("/mainsail/api/{**path}");
            Get("/mainsail/server/{**path}"); Post("/mainsail/server/{**path}"); Put("/mainsail/server/{**path}"); Patch("/mainsail/server/{**path}"); Delete("/mainsail/server/{**path}"); Head("/mainsail/server/{**path}"); Options("/mainsail/server/{**path}");
            Get("/mainsail/machine/{**path}"); Post("/mainsail/machine/{**path}"); Put("/mainsail/machine/{**path}"); Patch("/mainsail/machine/{**path}"); Delete("/mainsail/machine/{**path}"); Head("/mainsail/machine/{**path}"); Options("/mainsail/machine/{**path}");
            Get("/mainsail/access/{**path}"); Post("/mainsail/access/{**path}"); Put("/mainsail/access/{**path}"); Patch("/mainsail/access/{**path}"); Delete("/mainsail/access/{**path}"); Head("/mainsail/access/{**path}"); Options("/mainsail/access/{**path}");

            // top-level moonraker-compatible routes
            Get("/printer/{**path}"); Post("/printer/{**path}"); Put("/printer/{**path}"); Patch("/printer/{**path}"); Delete("/printer/{**path}"); Head("/printer/{**path}"); Options("/printer/{**path}");
            Get("/server/{**path}"); Post("/server/{**path}"); Put("/server/{**path}"); Patch("/server/{**path}"); Delete("/server/{**path}"); Head("/server/{**path}"); Options("/server/{**path}");
            Get("/machine/{**path}"); Post("/machine/{**path}"); Put("/machine/{**path}"); Patch("/machine/{**path}"); Delete("/machine/{**path}"); Head("/machine/{**path}"); Options("/machine/{**path}");
            Get("/access/{**path}"); Post("/access/{**path}"); Put("/access/{**path}"); Patch("/access/{**path}"); Delete("/access/{**path}"); Head("/access/{**path}"); Options("/access/{**path}");

            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var cfg = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<MoonrakerProxyEndpoint>>();
            var moonrakerBase = cfg.GetValue<string>("Moonraker:BaseUrl");
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

            logger.LogDebug("Proxy {Method} {ReqPath} -> moonraker={MoonrakerBase}, prefix={Prefix}", HttpContext.Request.Method, reqPath, moonrakerBase, targetPathPrefix);
            await ProxyUtil.ProxyRequest(HttpContext, moonrakerBase!, targetPathPrefix + path);
        }
    }
}
