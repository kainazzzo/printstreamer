using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;
using System;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class ShuffleEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Post("/api/audio/shuffle"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var a = HttpContext.RequestServices.GetRequiredService<AudioService>();
            var raw = HttpContext.Request.Query["enabled"].ToString();
            bool enabled = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1";
            a.SetShuffle(enabled);
            await HttpContext.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
        }
    }

    public class RepeatEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Post("/api/audio/repeat"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var a = HttpContext.RequestServices.GetRequiredService<AudioService>();
            var m = HttpContext.Request.Query["mode"].ToString();
            var mode = m?.ToLowerInvariant() switch { "one" => RepeatMode.One, "all" => RepeatMode.All, _ => RepeatMode.None };
            a.SetRepeat(mode);
            await HttpContext.Response.WriteAsJsonAsync(new { success = true, mode = mode.ToString() }, ct);
        }
    }
}
