using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Timelapse;
using System.IO;
using System.Linq;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Endpoints.Api.Timelapses
{
    public class TimelapseGenerateEndpoint : Endpoint<TimelapseNameRequest>
    {
        public override void Configure()
        {
            Post("/api/timelapses/{name}/generate");
            AllowAnonymous();
        }

        public override async Task HandleAsync(TimelapseNameRequest req, CancellationToken ct)
        {
            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<TimelapseGenerateEndpoint>>();
            try
            {
                var timelapseManager = HttpContext.RequestServices.GetRequiredService<TimelapseManager>();
                var timelapseDir = Path.Combine(timelapseManager.TimelapseDirectory, req.Name);
                if (!Directory.Exists(timelapseDir))
                {
                    HttpContext.Response.StatusCode = 404;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Timelapse not found" }, ct);
                    return;
                }

                var frameFiles = Directory.GetFiles(timelapseDir, "frame_*.jpg").OrderBy(f => f).ToArray();
                if (frameFiles.Length == 0)
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "No frames found" }, ct);
                    return;
                }

                var folderName = Path.GetFileName(timelapseDir);
                var videoPath = Path.Combine(timelapseDir, $"{folderName}.mp4");

                logger.LogInformation("Generating video from {FrameCount} frames: {VideoPath}", frameFiles.Length, videoPath);

                var arguments = $"-y -framerate 30 -start_number 0 -i \"{timelapseDir}/frame_%06d.jpg\" -vf \"tpad=stop_mode=clone:stop_duration=5\" -c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p -movflags +faststart \"{videoPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    HttpContext.Response.StatusCode = 500;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Failed to start ffmpeg" }, ct);
                    return;
                }

                var output = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);

                if (proc.ExitCode == 0 && File.Exists(videoPath))
                {
                    logger.LogInformation("Video created successfully: {VideoPath}", videoPath);
                    HttpContext.Response.StatusCode = 200;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = true, videoPath }, ct);
                }
                else
                {
                    logger.LogError("ffmpeg failed with exit code {ExitCode}", proc.ExitCode);
                    logger.LogError("ffmpeg output: {Output}", output);
                    HttpContext.Response.StatusCode = 500;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = $"ffmpeg failed with exit code {proc.ExitCode}" }, ct);
                }
            }
            catch (System.Exception ex)
            {
                logger.LogError(ex, "Error generating video: {Message}", ex.Message);
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
