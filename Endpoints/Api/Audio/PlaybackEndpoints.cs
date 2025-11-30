using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class PlayEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Post("/api/audio/play"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct) { var a = HttpContext.RequestServices.GetRequiredService<AudioService>(); a.Play(); await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct); }
    }

    public class PauseEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Post("/api/audio/pause"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct) { var a = HttpContext.RequestServices.GetRequiredService<AudioService>(); a.Pause(); await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct); }
    }

    public class ToggleEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Post("/api/audio/toggle"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct) { var a = HttpContext.RequestServices.GetRequiredService<AudioService>(); a.Toggle(); await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct); }
    }

    public class NextEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Post("/api/audio/next"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct) { var a = HttpContext.RequestServices.GetRequiredService<AudioService>(); a.Next(); try { var b = HttpContext.RequestServices.GetService<AudioBroadcastService>(); b?.InterruptFfmpeg(); } catch { } await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct); }
    }

    public class PrevEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Post("/api/audio/prev"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct) { var a = HttpContext.RequestServices.GetRequiredService<AudioService>(); a.Prev(); try { var b = HttpContext.RequestServices.GetService<AudioBroadcastService>(); b?.InterruptFfmpeg(); } catch { } await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct); }
    }
}
