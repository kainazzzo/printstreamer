using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;
using Microsoft.AspNetCore.Mvc;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class SetFolderEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Post("/api/audio/folder");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var path = HttpContext.Request.Query["path"].ToString();
            if (string.IsNullOrWhiteSpace(path)) { HttpContext.Response.StatusCode = 400; await HttpContext.Response.WriteAsJsonAsync(new { error = "Missing 'path'" }, ct); return; }
            var audio = HttpContext.RequestServices.GetRequiredService<AudioService>();
            audio.SetFolder(path);
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(new { success = true, folder = audio.Folder }, ct);
        }
    }
}
