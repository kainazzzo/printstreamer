using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Endpoints.Api.Overlay
{
    public class OverlayEnabledGetEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;

        public OverlayEnabledGetEndpoint(IConfiguration config)
        {
            _config = config;
        }

        public override void Configure() { Get("/api/overlay/enabled"); AllowAnonymous(); }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var enabled = _config.GetValue<bool?>("Overlay:Enabled") ?? true;
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { enabled }, ct);
        }
    }
}
