using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Timelapse;
using System.IO;
using System.Linq;
using System;

namespace PrintStreamer.Endpoints.Api.Timelapses
{
    public class TimelapseMetadataEndpoint : Endpoint<TimelapseNameRequest>
    {
        public override void Configure()
        {
            Get("/api/timelapses/{name}/metadata");
            AllowAnonymous();
        }

        public override async Task HandleAsync(TimelapseNameRequest req, CancellationToken ct)
        {
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

                var metadataPath = Path.Combine(timelapseDir, ".metadata");
                if (!File.Exists(metadataPath)
                    && (new[] { Path.Combine(timelapseDir, "metadata"), Path.Combine(timelapseDir, ".metadata.txt"), Path.Combine(timelapseDir, ".meta") }).FirstOrDefault(File.Exists) is string alt
                    && !string.IsNullOrEmpty(alt))
                {
                    metadataPath = alt;
                }

                if (!File.Exists(metadataPath))
                {
                    HttpContext.Response.StatusCode = 200;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = true, youtubeUrl = (string?)null, createdAt = (string?)null }, ct);
                    return;
                }

                string? youtubeUrl = null;
                DateTime? createdAt = null;
                var lines = File.ReadAllLines(metadataPath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var trimmed = line.Trim();
                    var eqIdx = trimmed.IndexOf('=');
                    var colonIdx = trimmed.IndexOf(':');
                    int sep = -1;
                    if (eqIdx >= 0) sep = eqIdx;
                    else if (colonIdx >= 0) sep = colonIdx;

                    if (sep >= 0)
                    {
                        var key = trimmed.Substring(0, sep).Trim();
                        var val = trimmed.Substring(sep + 1).Trim();
                        if (key.Equals("CreatedAt", StringComparison.OrdinalIgnoreCase))
                        {
                            if (DateTime.TryParse(val, out var dt)) createdAt = dt;
                        }
                        else if (key.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            youtubeUrl = val;
                        }
                    }
                    else
                    {
                        if (Uri.IsWellFormedUriString(trimmed, UriKind.Absolute) && trimmed.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0)
                            youtubeUrl = trimmed;
                    }
                }

                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { success = true, youtubeUrl, createdAt = createdAt?.ToString("O") }, ct);
            }
            catch (Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
