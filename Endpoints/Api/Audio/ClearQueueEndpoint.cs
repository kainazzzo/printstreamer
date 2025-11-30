using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class ClearQueueEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Post("/api/audio/clear");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var audio = HttpContext.RequestServices.GetRequiredService<AudioService>();
            audio.ClearQueue();
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { success = true }, ct);
        }
    }
}
