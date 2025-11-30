using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class ClearQueueEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        public ClearQueueEndpoint(AudioService audio) { _audio = audio; }
        public override void Configure()
        {
            Post("/api/audio/clear");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            _audio.ClearQueue();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct);
        }
    }
}
