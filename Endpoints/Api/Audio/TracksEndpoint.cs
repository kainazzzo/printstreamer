using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class TracksEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        public TracksEndpoint(AudioService audio) { _audio = audio; }
        public override void Configure()
        {
            Get("/api/audio/tracks");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var tracks = _audio.Library.Select(t => new { t.Name }).ToArray();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(tracks, ct);
        }
    }
}
