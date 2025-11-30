using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;

namespace PrintStreamer.Endpoints.Api.Debug
{
    public class PipelineEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Get("/api/debug/pipeline"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var sources = new Dictionary<string, string>
            {
                ["stage_1_source"] = "http://127.0.0.1:8080/stream/source",
                ["stage_2_overlay"] = "http://127.0.0.1:8080/stream/overlay",
                ["stage_3_audio"] = "http://127.0.0.1:8080/stream/audio",
                ["stage_4_mix"] = "http://127.0.0.1:8080/stream/mix",
                ["description"] = "Data flow pipeline endpoints (Stage 1→2→3→4→YouTube RTMP)"
            };
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(sources, ct);
        }
    }
}
