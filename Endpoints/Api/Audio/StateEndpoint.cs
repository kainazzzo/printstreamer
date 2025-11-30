using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class StateEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Get("/api/audio/state");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var audio = HttpContext.RequestServices.GetRequiredService<AudioService>();
            var st = audio.GetState();
            var result = new
            {
                IsPlaying = st.IsPlaying,
                Current = st.Current,
                Queue = st.Queue,
                Shuffle = st.Shuffle,
                Repeat = st.Repeat.ToString()
            };
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(result, ct);
        }
    }
}
