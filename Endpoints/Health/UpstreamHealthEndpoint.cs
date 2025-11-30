using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using PrintStreamer;

namespace PrintStreamer.Endpoints.Health
{
    public class UpstreamHealthEndpoint : EndpointWithoutRequest<object>
    {
        private readonly ILogger<UpstreamHealthEndpoint> _logger;
        private readonly IConfiguration _config;

        public UpstreamHealthEndpoint(ILogger<UpstreamHealthEndpoint> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        public override void Configure()
        {
            Get("/api/health/upstream");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var mainsail = _config.GetValue<string>("PrinterUI:MainsailUrl");
            var fluidd = _config.GetValue<string>("PrinterUI:FluiddUrl");
            var moon = _config.GetValue<string>("Moonraker:BaseUrl");
            var results = new Dictionary<string, object?>();

            async Task<object> ProbeHttp(string? url)
            {
                if (string.IsNullOrWhiteSpace(url)) return "not-configured";
                try
                {
                    using var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                    using var resp = await ProxyUtil.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    return new { status = (int)resp.StatusCode, reason = resp.ReasonPhrase };
                }
                catch (OperationCanceledException) { return "timeout/canceled"; }
                catch (System.Exception ex) { return ex.Message; }
            }

            results["mainsail"] = await ProbeHttp(mainsail);
            results["fluidd"] = await ProbeHttp(fluidd);
            results["moonraker_http"] = await ProbeHttp(moon);

            try
            {
                if (!string.IsNullOrWhiteSpace(moon))
                {
                    var ub = new System.UriBuilder(moon);
                    var host = ub.Host;
                    var port = ub.Port > 0 ? ub.Port : (ub.Scheme == "https" ? 443 : 80);
                    using var tcp = new System.Net.Sockets.TcpClient();
                    var connectTask = tcp.ConnectAsync(host, port);
                    var completed = await Task.WhenAny(connectTask, Task.Delay(System.TimeSpan.FromSeconds(3), ct));
                    results["moonraker_tcp"] = completed == connectTask && tcp.Connected ? "open" : "closed/timeout";
                }
                else results["moonraker_tcp"] = "not-configured";
            }
            catch (System.Exception ex)
            {
                results["moonraker_tcp"] = ex.Message;
            }

            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(results, ct);
        }
    }
}
