using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class PlayEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        public PlayEndpoint(AudioService audio) { _audio = audio; }
        public override void Configure() { Post("/api/audio/play"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct) { _audio.Play(); await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct); }
    }

    public class PauseEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        public PauseEndpoint(AudioService audio) { _audio = audio; }
        public override void Configure() { Post("/api/audio/pause"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct) { _audio.Pause(); await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct); }
    }

    public class ToggleEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        public ToggleEndpoint(AudioService audio) { _audio = audio; }
        public override void Configure() { Post("/api/audio/toggle"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct) { _audio.Toggle(); await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct); }
    }

    public class NextEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        private readonly AudioBroadcastService? _broadcaster;
        public NextEndpoint(AudioService audio, AudioBroadcastService? broadcaster = null) { _audio = audio; _broadcaster = broadcaster; }
        public override void Configure() { Post("/api/audio/next"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct) { _audio.Next(); try { _broadcaster?.InterruptFfmpeg(); } catch { } await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct); }
    }

    public class PrevEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        private readonly AudioBroadcastService? _broadcaster;
        public PrevEndpoint(AudioService audio, AudioBroadcastService? broadcaster = null) { _audio = audio; _broadcaster = broadcaster; }
        public override void Configure() { Post("/api/audio/prev"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct) { _audio.Prev(); try { _broadcaster?.InterruptFfmpeg(); } catch { } await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct); }
    }
}
