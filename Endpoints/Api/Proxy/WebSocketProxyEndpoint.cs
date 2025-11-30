using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Endpoints.Api.Proxy
{
    public class WebSocketProxyEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<WebSocketProxyEndpoint> _logger;

        public WebSocketProxyEndpoint(IConfiguration cfg, ILogger<WebSocketProxyEndpoint> logger)
        {
            _cfg = cfg;
            _logger = logger;
        }

        public override void Configure()
        {
            Get("/websocket");
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

            if (!HttpContext.WebSockets.IsWebSocketRequest)
            {
                HttpContext.Response.StatusCode = 400;
                await HttpContext.Response.WriteAsync("Expected WebSocket request", ct);
                return;
            }

            var ub = new UriBuilder(moonrakerBase);
            if (string.Equals(ub.Scheme, "http", StringComparison.OrdinalIgnoreCase)) ub.Scheme = "ws";
            else if (string.Equals(ub.Scheme, "https", StringComparison.OrdinalIgnoreCase)) ub.Scheme = "wss";
            ub.Path = ub.Path.TrimEnd('/') + "/websocket";
            var qs = HttpContext.Request.QueryString.HasValue ? HttpContext.Request.QueryString.Value : null;
            if (!string.IsNullOrEmpty(qs)) ub.Query = qs!.TrimStart('?');
            var upstreamUri = ub.Uri;

            ClientWebSocket? upstream = null;
            Exception? lastConnectEx = null;
            var maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                upstream = new ClientWebSocket();
                upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                var allowed = new[] { "Origin", "Cookie", "Authorization" };
                try
                {
                    var hdrNames = string.Join(",", HttpContext.Request.Headers.Select(h => h.Key).Where(k => allowed.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray());
                    _logger.LogDebug("Forwarding request headers to upstream (names): {HeaderNames}", hdrNames);
                }
                catch { }

                foreach (var name in allowed)
                {
                    if (HttpContext.Request.Headers.TryGetValue(name, out var vals))
                    {
                        try { upstream.Options.SetRequestHeader(name, vals.ToString()); } catch { }
                    }
                }

                if (!HttpContext.Request.Headers.ContainsKey("Origin"))
                {
                    try
                    {
                        var defaultOrigin = HttpContext.Request.Scheme + "://" + HttpContext.Request.Host.Value;
                        upstream.Options.SetRequestHeader("Origin", defaultOrigin);
                        _logger.LogDebug("Set default Origin header for upstream: {Origin}", defaultOrigin);
                    }
                    catch { }
                }

                if (HttpContext.Request.Headers.TryGetValue("Sec-WebSocket-Protocol", out var protocols))
                {
                    foreach (var proto in protocols)
                    {
                        if (proto == null) continue;
                        foreach (var p in proto.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var trimmed = p.Trim();
                            if (!string.IsNullOrEmpty(trimmed))
                            {
                                try { upstream.Options.AddSubProtocol(trimmed); } catch { }
                            }
                        }
                    }
                }

                _logger.LogDebug("Connecting upstream attempt {Attempt}/{MaxAttempts} {UpstreamUri} (Origin={Origin})", attempt, maxAttempts, upstreamUri, HttpContext.Request.Headers["Origin"]);
                try
                {
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await upstream.ConnectAsync(upstreamUri, connectCts.Token);
                    lastConnectEx = null;
                    break;
                }
                catch (OperationCanceledException oce)
                {
                    if (ct.IsCancellationRequested)
                    {
                        _logger.LogDebug("Client aborted websocket request while connecting to upstream {UpstreamUri}", upstreamUri);
                        try { upstream.Dispose(); } catch { }
                        return;
                    }
                    lastConnectEx = oce;
                    _logger.LogWarning(oce, "Upstream connect attempt {Attempt} timed out for {UpstreamUri}", attempt, upstreamUri);
                    try { upstream.Dispose(); } catch { }
                    upstream = null;
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500));
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    lastConnectEx = ex;
                    _logger.LogWarning(ex, "Upstream connect attempt {Attempt} failed for {UpstreamUri}", attempt, upstreamUri);
                    try { upstream.Dispose(); } catch { }
                    upstream = null;
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500));
                        continue;
                    }
                }
            }

            if (upstream == null)
            {
                if (lastConnectEx is OperationCanceledException && ct.IsCancellationRequested)
                {
                    _logger.LogDebug("Aborted upstream websocket connect after downstream cancellation: {UpstreamUri}", upstreamUri);
                    return;
                }

                _logger.LogWarning(lastConnectEx, "Upstream connect failed after {Attempts} attempts: {UpstreamUri}", maxAttempts, upstreamUri);
                using var downstreamErr = await HttpContext.WebSockets.AcceptWebSocketAsync();
                try
                {
                    var errorMsg = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        error = new { code = -32000, message = $"Moonraker connection failed: {lastConnectEx?.Message ?? "Unknown error"}" }
                    });
                    var errorBytes = System.Text.Encoding.UTF8.GetBytes(errorMsg);
                    await downstreamErr.SendAsync(new ArraySegment<byte>(errorBytes), WebSocketMessageType.Text, true, ct);
                }
                catch { }
                try { await downstreamErr.CloseAsync(WebSocketCloseStatus.InternalServerError, "Upstream connection failed", CancellationToken.None); } catch { }
                return;
            }

            _logger.LogInformation("Upstream WebSocket connected. Upstream chosen subprotocol: {SubProtocol}", upstream.SubProtocol ?? "<none>");
            using var downstream = await HttpContext.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
            _logger.LogDebug("Upstream connected, starting bidirectional tunnel (subprotocol={SubProtocol})", upstream.SubProtocol);

            var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pump1 = ProxyUtil.PumpWebSocket(downstream, upstream, cts2.Token);
            var pump2 = ProxyUtil.PumpWebSocket(upstream, downstream, cts2.Token);
            await Task.WhenAny(pump1, pump2);
            cts2.Cancel();
            _logger.LogDebug("Tunnel closed");
        }
    }
}
