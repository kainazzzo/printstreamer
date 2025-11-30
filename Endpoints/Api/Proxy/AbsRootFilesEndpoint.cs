using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Endpoints.Api.Proxy
{
    public class AbsRootFilesEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<AbsRootFilesEndpoint> _logger;

        public AbsRootFilesEndpoint(IConfiguration cfg, ILogger<AbsRootFilesEndpoint> logger)
        {
            _cfg = cfg;
            _logger = logger;
        }

        public override void Configure()
        {
            Get("/assets/{**path}", "/img/{**path}", "/manifest.webmanifest", "/sw.js");
            Head("/assets/{**path}", "/img/{**path}", "/manifest.webmanifest", "/sw.js");
            Options("/assets/{**path}", "/img/{**path}", "/manifest.webmanifest", "/sw.js");

            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var referer = HttpContext.Request.Headers.Referer.ToString();
            var fluidd = _cfg.GetValue<string>("PrinterUI:FluiddUrl");
            var mainsail = _cfg.GetValue<string>("PrinterUI:MainsailUrl");
            string? target = null;
            if (!string.IsNullOrWhiteSpace(referer) && referer.Contains("/mainsail", System.StringComparison.OrdinalIgnoreCase)) target = mainsail;
            else if (!string.IsNullOrWhiteSpace(referer) && referer.Contains("/fluidd", System.StringComparison.OrdinalIgnoreCase)) target = fluidd;
            if (string.IsNullOrWhiteSpace(target)) target = fluidd ?? mainsail;
            if (string.IsNullOrWhiteSpace(target))
            {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.Response.WriteAsync("UI target not configured", ct);
                return;
            }

            var path = Route<string?>("path") ?? string.Empty;
            var absolutePath = HttpContext.Request.Path.Value?.TrimStart('/') ?? string.Empty;

            // For assets and images, forward to assets/ or img/ respectively
            if (HttpContext.Request.Path.Value != null && HttpContext.Request.Path.Value.StartsWith("/assets", System.StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("ABS-ROOT /assets/{Path} via referer='{Referer}' -> {Target}", path ?? string.Empty, referer, target);
                await ProxyUtil.ProxyRequest(HttpContext, target, "assets/" + path);
                return;
            }
            else if (HttpContext.Request.Path.Value != null && HttpContext.Request.Path.Value.StartsWith("/img", System.StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("ABS-ROOT /img/{Path} via referer='{Referer}' -> {Target}", path ?? string.Empty, referer, target);
                await ProxyUtil.ProxyRequest(HttpContext, target, "img/" + path);
                return;
            }
            else if (absolutePath == "manifest.webmanifest")
            {
                _logger.LogDebug("ABS-ROOT /manifest.webmanifest via referer='{Referer}' -> {Target}", referer, target);
                await ProxyUtil.ProxyRequest(HttpContext, target, "manifest.webmanifest");
                return;
            }
            else if (absolutePath == "sw.js")
            {
                _logger.LogDebug("ABS-ROOT /sw.js via referer='{Referer}' -> {Target}", referer, target);
                await ProxyUtil.ProxyRequest(HttpContext, target, "sw.js");
                return;
            }
            else
            {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.Response.WriteAsync("Resource not handled", ct);
            }
        }
    }
}
