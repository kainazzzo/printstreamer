using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class BroadcastStatusEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Get("/api/audio/broadcast/status"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var b = HttpContext.RequestServices.GetRequiredService<AudioBroadcastService>();
            var status = b.GetStatus();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(status, ct);
        }
    }
}
