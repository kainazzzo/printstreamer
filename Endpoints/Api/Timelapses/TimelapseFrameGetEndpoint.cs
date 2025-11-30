using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Timelapse;
using System.IO;

namespace PrintStreamer.Endpoints.Api.Timelapses
{
    public class TimelapseFrameGetEndpoint : Endpoint<TimelapseNameFileRequest>
    {
        private readonly TimelapseManager _timelapseManager;

        public TimelapseFrameGetEndpoint(TimelapseManager timelapseManager)
        {
            _timelapseManager = timelapseManager;
        }

        public override void Configure()
        {
            Get("/api/timelapses/{name}/frames/{filename}");
            AllowAnonymous();
        }

        public override async Task HandleAsync(TimelapseNameFileRequest req, CancellationToken ct)
        {
            try
            {
                var timelapseDir = Path.Combine(_timelapseManager.TimelapseDirectory, req.Name);
                var filePath = Path.Combine(timelapseDir, req.Filename);

                try
                {
                    var fullTimelapseDir = Path.GetFullPath(timelapseDir);
                    var fullFilePath = Path.GetFullPath(filePath);
                    var dirPathWithSep = fullTimelapseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!fullFilePath.StartsWith(dirPathWithSep, System.StringComparison.OrdinalIgnoreCase) && !string.Equals(fullFilePath, fullTimelapseDir, System.StringComparison.OrdinalIgnoreCase))
                    {
                        HttpContext.Response.StatusCode = 404;
                        return;
                    }
                    if (!File.Exists(fullFilePath))
                    {
                        HttpContext.Response.StatusCode = 404;
                        return;
                    }

                    filePath = fullFilePath;
                }
                catch
                {
                    HttpContext.Response.StatusCode = 404;
                    return;
                }

                var contentType = req.Filename.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase) ? "image/jpeg" :
                                  req.Filename.EndsWith(".mp4", System.StringComparison.OrdinalIgnoreCase) ? "video/mp4" :
                                  "application/octet-stream";

                HttpContext.Response.ContentType = contentType;
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.SendFileAsync(filePath, 0, null, ct);
            }
            catch
            {
                HttpContext.Response.StatusCode = 404;
            }
        }
    }
}
