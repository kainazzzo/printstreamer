using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class BroadcastStatusEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioBroadcastService _broadcaster;
        public BroadcastStatusEndpoint(AudioBroadcastService broadcaster) { _broadcaster = broadcaster; }
        public override void Configure() { Get("/api/audio/broadcast/status"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var status = _broadcaster.GetStatus();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(status, ct);
        }
    }
}
