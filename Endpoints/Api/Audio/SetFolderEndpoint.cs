using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;
using Microsoft.AspNetCore.Mvc;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class SetFolderEndpoint : EndpointWithoutRequest<object>
    {
        private readonly AudioService _audio;
        public SetFolderEndpoint(AudioService audio) { _audio = audio; }
        public override void Configure()
        {
            Post("/api/audio/folder");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var path = HttpContext.Request.Query["path"].ToString();
            if (string.IsNullOrWhiteSpace(path)) { HttpContext.Response.StatusCode = 400; await HttpContext.Response.WriteAsJsonAsync(new { error = "Missing 'path'" }, ct); return; }
            _audio.SetFolder(path);
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { success = true, folder = _audio.Folder }, ct);
        }
    }
}
