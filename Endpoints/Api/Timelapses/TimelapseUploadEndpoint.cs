using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Timelapse;
using PrintStreamer.Services;
using System.IO;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Endpoints.Api.Timelapses
{
    public class TimelapseUploadEndpoint : Endpoint<TimelapseNameRequest>
    {
        private readonly ILogger<TimelapseUploadEndpoint> _logger;
        private readonly TimelapseManager _timelapseManager;
        private readonly YouTubeControlService _ytService;

        public TimelapseUploadEndpoint(ILogger<TimelapseUploadEndpoint> logger, TimelapseManager timelapseManager, YouTubeControlService ytService)
        {
            _logger = logger;
            _timelapseManager = timelapseManager;
            _ytService = ytService;
        }

        public override void Configure()
        {
            Post("/api/timelapses/{name}/upload");
            AllowAnonymous();
        }

        public override async Task HandleAsync(TimelapseNameRequest req, CancellationToken ct)
        {
            try
            {
                var timelapseDir = Path.Combine(_timelapseManager.TimelapseDirectory, req.Name);
                if (!Directory.Exists(timelapseDir))
                {
                    HttpContext.Response.StatusCode = 404;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Timelapse not found" }, ct);
                    return;
                }

                var videoFiles = Directory.GetFiles(timelapseDir, "*.mp4");
                if (videoFiles.Length == 0)
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "No video file found" }, ct);
                    return;
                }

                var videoPath = videoFiles[0];
                _logger.LogInformation("HTTP /api/timelapses/{Name}/upload request received", req.Name);
                _logger.LogDebug("Timelapse dir: {TimelapseDir}; video: {VideoPath}", timelapseDir, videoPath);
                if (!await _ytService.AuthenticateAsync(ct))
                {
                    HttpContext.Response.StatusCode = 401;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "YouTube authentication failed" }, ct);
                    return;
                }

                _logger.LogInformation("Starting YouTube upload for timelapse {Name}", req.Name);
                var videoId = await _ytService.UploadTimelapseVideoAsync(videoPath, req.Name, ct, true);
                _logger.LogInformation("YouTube upload result videoId={VideoId}", videoId);

                if (!string.IsNullOrEmpty(videoId))
                {
                    var url = $"https://www.youtube.com/watch?v={videoId}";
                    try
                    {
                        var metadataPath = Path.Combine(timelapseDir, ".metadata");
                        var existingLines = File.Exists(metadataPath) ? File.ReadAllLines(metadataPath).ToList() : new System.Collections.Generic.List<string>();
                        existingLines.RemoveAll(line => line.StartsWith("YouTubeUrl="));
                        existingLines.Add($"YouTubeUrl={url}");
                        File.WriteAllLines(metadataPath, existingLines);
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to save YouTube URL to metadata: {Message}", ex.Message);
                    }

                    HttpContext.Response.StatusCode = 200;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = true, videoId, url }, ct);
                    return;
                }

                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Upload failed" }, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error uploading timelapse: {Message}", ex.Message);
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
