using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class TracksEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Get("/api/audio/tracks");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var audio = HttpContext.RequestServices.GetRequiredService<AudioService>();
            var tracks = audio.Library.Select(t => new { t.Name }).ToArray();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(tracks, ct);
        }
    }
}
