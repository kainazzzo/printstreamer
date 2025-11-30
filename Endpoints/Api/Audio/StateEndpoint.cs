using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class StateEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        public StateEndpoint(AudioService audio) { _audio = audio; }
        public override void Configure()
        {
            Get("/api/audio/state");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var st = _audio.GetState();
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
