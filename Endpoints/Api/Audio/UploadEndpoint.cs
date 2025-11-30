using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;
using System.IO;
using System;
using System.Linq;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class UploadEndpoint : EndpointWithoutRequest<object>
    {
        private readonly ILogger<UploadEndpoint> _logger;
        private readonly AudioService _audio;
        private readonly IConfiguration _config;
        public UploadEndpoint(ILogger<UploadEndpoint> logger, AudioService audio, IConfiguration config) { _logger = logger; _audio = audio; _config = config; }
        public override void Configure()
        {
            Post("/api/audio/upload");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                // Allow larger uploads per request
                var maxUploadBytes = 300L * 1024L * 1024L;
                try
                {
                    var maxReqFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                    if (maxReqFeature != null && !maxReqFeature.IsReadOnly)
                    {
                        maxReqFeature.MaxRequestBodySize = maxUploadBytes;
                        _logger.LogDebug("Set per-request MaxRequestBodySize={MaxBytes}", maxUploadBytes);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not set per-request MaxRequestBodySize (continuing)");
                }

                _logger.LogDebug("Incoming upload: Content-Length={ContentLength}, HasForm={HasForm}", HttpContext.Request.ContentLength, HttpContext.Request.HasFormContentType);
                if (!HttpContext.Request.HasFormContentType)
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Expected multipart/form-data" }, ct);
                    return;
                }

                var form = await HttpContext.Request.ReadFormAsync(ct);
                var file = form.Files["file"] ?? form.Files.FirstOrDefault();
                if (file == null || file.Length == 0)
                {
                    _logger.LogWarning("Upload request missing file");
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "No file provided" }, ct);
                    return;
                }

                var origFileName = Path.GetFileName(file.FileName ?? "upload");
                _logger.LogInformation("Upload received: name={FileName}, length={Length}, contentType={ContentType}", origFileName, file.Length, file.ContentType);

                if (string.IsNullOrWhiteSpace(origFileName))
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Invalid file name" }, ct);
                    return;
                }

                var allowedExt = new[] { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus" };
                var ext = Path.GetExtension(origFileName).ToLowerInvariant();
                if (!Array.Exists(allowedExt, a => a == ext))
                {
                    _logger.LogWarning("Rejected upload with unsupported extension: {Ext}", ext);
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = $"Unsupported file extension '{ext}'" }, ct);
                    return;
                }

                const long maxBytes = 200L * 1024L * 1024L;
                if (file.Length > maxBytes)
                {
                    _logger.LogWarning("Rejected upload too large: {Length} > {Max}", file.Length, maxBytes);
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "File too large" }, ct);
                    return;
                }

                var folder = _audio.Folder;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    folder = _config.GetValue<string>("Audio:Folder") ?? "audio";
                }

                folder = Path.GetFullPath(folder);
                _logger.LogDebug("Resolved audio folder: {Folder}", folder);

                try
                {
                    Directory.CreateDirectory(folder);
                }
                    catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create audio folder: {Folder}", folder);
                    HttpContext.Response.StatusCode = 500;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Failed to create destination folder" }, ct);
                    return;
                }

                try
                {
                    var tmpTest = Path.Combine(folder, $".write_test_{Guid.NewGuid():N}.tmp");
                    await File.WriteAllTextAsync(tmpTest, "x");
                    File.Delete(tmpTest);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Audio folder not writable: {Folder}", folder);
                    HttpContext.Response.StatusCode = 500;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Destination folder not writable" }, ct);
                    return;
                }

                var destName = origFileName;
                var destPath = Path.Combine(folder, destName);
                var attempt = 1;
                while (File.Exists(destPath))
                {
                    destName = Path.GetFileNameWithoutExtension(origFileName) + $" ({attempt})" + Path.GetExtension(origFileName);
                    destPath = Path.Combine(folder, destName);
                    attempt++;
                    if (attempt > 100) break;
                }

                // Explicit buffered copy
                try
                {
                    await using var destFs = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                    await using var src = file.OpenReadStream();
                    _logger.LogDebug("Starting copy: srcLength={SrcLen}, destPath={Dest}", src.Length, destPath);

                    var buffer = new byte[64 * 1024];
                    int read;
                    long total = 0;
                    while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await destFs.WriteAsync(buffer.AsMemory(0, read), ct);
                        total += read;
                    }

                    await destFs.FlushAsync(ct);
                    _logger.LogInformation("Copy complete: wrote {Bytes} bytes to {Dest}", total, destPath);
                }
                    catch (OperationCanceledException oce)
                {
                    _logger.LogWarning(oce, "Upload canceled while copying to disk: {Dest}", destPath);
                    try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                    HttpContext.Response.StatusCode = 499; // client closed request (best-effort)
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Upload canceled" }, ct);
                    return;
                }
                    catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed while copying uploaded file to {Dest}", destPath);
                    try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                    HttpContext.Response.StatusCode = 500;
                    await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = "Failed saving file: " + ex.Message }, ct);
                    return;
                }

                try
                {
                    _audio.Rescan();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Rescan failed after upload; uploaded file saved at {Path}", destPath);
                }

                _logger.LogInformation("Uploaded audio file saved: {Path}", destPath);
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { success = true, filename = destName, path = destPath }, ct);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in /api/audio/upload");
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
                return;
            }
        }
    }
}
