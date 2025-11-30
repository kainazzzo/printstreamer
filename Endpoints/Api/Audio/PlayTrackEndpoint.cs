using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;
using System.IO;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class PlayTrackEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Post("/api/audio/play-track"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var name = HttpContext.Request.Query["name"].ToString();
            if (string.IsNullOrWhiteSpace(name)) { HttpContext.Response.StatusCode = 400; await HttpContext.Response.WriteAsJsonAsync(new { error = "Missing 'name'" }, ct); return; }
            var audio = HttpContext.RequestServices.GetRequiredService<AudioService>();
            if (!audio.TrySelectByName(name, out var selected)) { await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Track not found" }, ct); return; }
            try { var b = HttpContext.RequestServices.GetService<AudioBroadcastService>(); b?.InterruptFfmpeg(); } catch { }
            await HttpContext.Response.WriteAsJsonAsync(new { success = true, track = Path.GetFileName(selected!) }, ct);
        }
    }
}
