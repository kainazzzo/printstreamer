using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;
using System;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class ShuffleEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        public ShuffleEndpoint(AudioService audio) { _audio = audio; }
        public override void Configure() { Post("/api/audio/shuffle"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var raw = HttpContext.Request.Query["enabled"].ToString();
            bool enabled = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1";
            _audio.SetShuffle(enabled);
            await HttpContext.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
        }
    }

    public class RepeatEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        public RepeatEndpoint(AudioService audio) { _audio = audio; }
        public override void Configure() { Post("/api/audio/repeat"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var m = HttpContext.Request.Query["mode"].ToString();
            var mode = m?.ToLowerInvariant() switch { "one" => RepeatMode.One, "all" => RepeatMode.All, _ => RepeatMode.None };
            _audio.SetRepeat(mode);
            await HttpContext.Response.WriteAsJsonAsync(new { success = true, mode = mode.ToString() }, ct);
        }
    }
}
