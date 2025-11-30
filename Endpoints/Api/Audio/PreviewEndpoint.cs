using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;
using System.IO;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class PreviewEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        public PreviewEndpoint(AudioService audio) { _audio = audio; }
        public override void Configure() { Get("/api/audio/preview"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                var name = HttpContext.Request.Query["name"].ToString();
                if (string.IsNullOrWhiteSpace(name)) { HttpContext.Response.StatusCode = 400; await HttpContext.Response.WriteAsJsonAsync(new { error = "Missing 'name'" }, ct); return; }
                var path = _audio.GetPathForName(name);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { HttpContext.Response.StatusCode = 404; return; }

                var ext = Path.GetExtension(path).ToLowerInvariant();
                var contentType = ext switch
                {
                    ".mp3" => "audio/mpeg",
                    ".aac" => "audio/aac",
                    ".m4a" => "audio/mp4",
                    ".wav" => "audio/wav",
                    ".flac" => "audio/flac",
                    ".ogg" => "audio/ogg",
                    ".opus" => "audio/ogg",
                    _ => "application/octet-stream"
                };

                // Return the file with range support
                await Results.File(path, contentType, enableRangeProcessing: true).ExecuteAsync(HttpContext);
            }
            catch
            {
                HttpContext.Response.StatusCode = 404;
            }
        }
    }
}
