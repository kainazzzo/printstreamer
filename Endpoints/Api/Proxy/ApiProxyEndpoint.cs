using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using PrintStreamer;

namespace PrintStreamer.Endpoints.Api.Proxy
{
    public class ApiProxyEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<ApiProxyEndpoint> _logger;

        public ApiProxyEndpoint(IConfiguration cfg, ILogger<ApiProxyEndpoint> logger)
        {
            _cfg = cfg;
            _logger = logger;
        }

        public override void Configure()
        {
            Get("/api/{**path}");
            Post("/api/{**path}");
            Put("/api/{**path}");
            Patch("/api/{**path}");
            Delete("/api/{**path}");
            Head("/api/{**path}");
            Options("/api/{**path}");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var path = Route<string?>("path") ?? string.Empty;
            var moonrakerBase = _cfg.GetValue<string>("Moonraker:BaseUrl");
            if (string.IsNullOrWhiteSpace(moonrakerBase))
            {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.Response.WriteAsync("Moonraker base URL not configured", ct);
                return;
            }

            _logger.LogDebug("Proxy /api/{Path} -> {Target}", path, moonrakerBase);
            try
            {
                await ProxyUtil.ProxyRequest(HttpContext, moonrakerBase!, "api/" + path);
            }
            catch (OperationCanceledException)
            {
                // upstream operation cancelled - let pipeline return proper status
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning(ex, "API proxy error for {Path} to {Target}", path, moonrakerBase);
                if (!HttpContext.Response.HasStarted)
                {
                    HttpContext.Response.StatusCode = 502;
                    await HttpContext.Response.WriteAsync("API proxy error: " + ex.Message, ct);
                }
            }
        }
    }
}
