using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;
using System.Net.Http;

namespace PrintStreamer.Endpoints.Stream
{
    public class OverlayEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;
        private readonly OverlayBroadcastService _overlayBroadcastService;
        private readonly ILogger<OverlayEndpoint> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public OverlayEndpoint(IConfiguration config, OverlayBroadcastService overlayBroadcastService, ILogger<OverlayEndpoint> logger, IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _overlayBroadcastService = overlayBroadcastService;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }
        public override void Configure()
        {
            Get("/stream/overlay");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var enabled = _config.GetValue<bool?>("Overlay:Enabled") ?? true;
            if (!enabled)
            {
                // Overlay is disabled - return 503 Service Unavailable
                HttpContext.Response.StatusCode = 503;
                await HttpContext.Response.WriteAsync("Overlay stream is disabled", ct);
                return;
            }

            try
            {
                var started = await _overlayBroadcastService.EnsureRunningAsync(ct);
                if (!started)
                {
                    HttpContext.Response.StatusCode = 503;
                    await HttpContext.Response.WriteAsync("Overlay stream unavailable", ct);
                    return;
                }

                var client = _httpClientFactory.CreateClient(nameof(OverlayEndpoint));
                client.Timeout = Timeout.InfiniteTimeSpan;

                using var upstream = await client.GetAsync(_overlayBroadcastService.OutputUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!upstream.IsSuccessStatusCode)
                {
                    HttpContext.Response.StatusCode = (int)upstream.StatusCode;
                    await HttpContext.Response.WriteAsync("Overlay upstream error", ct);
                    return;
                }

                HttpContext.Response.StatusCode = (int)upstream.StatusCode;
                HttpContext.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "multipart/x-mixed-replace";
                HttpContext.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                HttpContext.Response.Headers["Pragma"] = "no-cache";

                await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
                await upstreamStream.CopyToAsync(HttpContext.Response.Body, 64 * 1024, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "[OverlayEndpoint] Error proxying overlay stream");
                if (!HttpContext.Response.HasStarted)
                {
                    HttpContext.Response.StatusCode = 500;
                    await HttpContext.Response.WriteAsync("Overlay streamer error: " + ex.Message);
                }
            }
        }
    }
}
