using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class ScanEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        public ScanEndpoint(AudioService audio) { _audio = audio; }
        public override void Configure()
        {
            Post("/api/audio/scan");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            _audio.Rescan();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct);
        }
    }
}
