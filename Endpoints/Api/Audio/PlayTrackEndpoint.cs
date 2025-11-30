using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;
using System.IO;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class PlayTrackEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        private readonly AudioBroadcastService? _broadcaster;
        public PlayTrackEndpoint(AudioService audio, AudioBroadcastService? broadcaster = null) { _audio = audio; _broadcaster = broadcaster; }
        public override void Configure() { Post("/api/audio/play-track"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var name = HttpContext.Request.Query["name"].ToString();
            if (string.IsNullOrWhiteSpace(name)) { HttpContext.Response.StatusCode = 400; await HttpContext.Response.WriteAsJsonAsync(new { error = "Missing 'name'" }, ct); return; }
            if (!_audio.TrySelectByName(name, out var selected)) { await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Track not found" }, ct); return; }
            try { _broadcaster?.InterruptFfmpeg(); } catch { }
            await HttpContext.Response.WriteAsJsonAsync(new { success = true, track = Path.GetFileName(selected!) }, ct);
        }
    }
}
