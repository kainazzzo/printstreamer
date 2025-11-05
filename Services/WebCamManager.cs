using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Services
{
    public class WebCamManager
    {
        private readonly IConfiguration _config;
        private readonly HttpClient _httpClient;
        private readonly ILogger<WebCamManager> _logger;
        private volatile bool _disabled = false;
        // Track active client cancellation tokens so we can cancel upstream copy when disabling
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _activeClients = new System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource>();

        // Fallback black JPEG loaded from file
        private readonly byte[] _blackJpeg;

        public WebCamManager(IConfiguration config, ILogger<WebCamManager> logger)
        {
            _config = config;
            _logger = logger;
            _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            
            // Load fallback_black.jpg from filesystem
            var fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), "fallback_black.jpg");
            if (File.Exists(fallbackPath))
            {
                _blackJpeg = File.ReadAllBytes(fallbackPath);
                _logger.LogInformation("[WebCamManager] Loaded fallback image: {Bytes} bytes", _blackJpeg.Length);
            }
            else
            {
                // Minimal black JPEG as ultimate fallback if file doesn't exist
                _blackJpeg = Convert.FromBase64String(
                    "/9j/4AAQSkZJRgABAQAAAQABAAD/2wCEAAICAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGBggHBwcHBw0JCQgKCAgJCgsMDAwMDAwMDAwMDAwMDAz/wAALCAABAAEBAREA/8QAFQABAQAAAAAAAAAAAAAAAAAAAAb/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAgP/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwD9AP/Z");
                _logger.LogWarning("[WebCamManager] fallback_black.jpg not found, using hardcoded fallback");
            }
        }

        public bool IsDisabled => _disabled;

        public void SetDisabled(bool disabled)
        {
            _disabled = disabled;
            if (disabled)
            {
                // Cancel all active upstream copies so clients will fall back to the black loop
                foreach (var kv in _activeClients)
                {
                    try { kv.Value.Cancel(); } catch { }
                }
            }
        }

        public void Toggle()
        {
            SetDisabled(!_disabled);
        }

        public async Task HandleStreamRequest(HttpContext ctx)
        {
            // Support one-shot snapshot via /stream?action=snapshot
            var action = ctx.Request.Query["action"].ToString();
            if (string.Equals(action, "snapshot", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSnapshotRequest(ctx);
                return;
            }

            var clientId = Guid.NewGuid();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            _activeClients[clientId] = linkedCts;

            var source = _config.GetValue<string>("Stream:Source");
            _logger.LogInformation("[WebCamManager] Client connected: {Remote}:{Port}", ctx.Connection.RemoteIpAddress, ctx.Connection.RemotePort);

            if (_disabled)
            {
                // If manager is disabled, do not attempt any upstream connections — just serve the black fallback loop.
                try { await ServeBlackFallback(ctx, probeUpstream: false); } finally { _activeClients.TryRemove(clientId, out _); }
                return;
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, source);
                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
                resp.EnsureSuccessStatusCode();

                ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                ctx.Response.StatusCode = 200;

                using var upstream = await resp.Content.ReadAsStreamAsync(linkedCts.Token);
                // Copy using the linked cancellation token so we can cancel on disable
                await upstream.CopyToAsync(ctx.Response.Body, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[WebCamManager] Client disconnected or request canceled.");
                // If we were canceled because the manager disabled the webcam, serve fallback instead
                if (_disabled && !ctx.RequestAborted.IsCancellationRequested)
                {
                    // If we were canceled due to disabling the manager, ensure we serve the black fallback and do not probe upstream.
                    try { await ServeBlackFallback(ctx, probeUpstream: false); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WebCamManager] Upstream error: {Message} - serving fallback MJPEG", ex.Message);
                // On general errors, start the fallback loop which will periodically probe the upstream for recovery.
                try { await ServeBlackFallback(ctx, probeUpstream: true); } catch { }
            }
            finally
            {
                // Clean up client registration
                try { linkedCts.Cancel(); } catch { }
                linkedCts.Dispose();
                _activeClients.TryRemove(clientId, out _);
            }
        }

        private async Task HandleSnapshotRequest(HttpContext ctx)
        {
            try
            {
                var source = _config.GetValue<string>("Stream:Source");

                // If disabled, return the fallback black JPEG immediately
                if (_disabled)
                {
                    await WriteJpegAsync(ctx, _blackJpeg);
                    return;
                }

                // 1) Try upstream snapshot endpoint if available by swapping/adding action=snapshot
                if (Uri.TryCreate(source, UriKind.Absolute, out var srcUri))
                {
                    var ub = new UriBuilder(srcUri);
                    var query = System.Web.HttpUtility.ParseQueryString(ub.Query);
                    // Prefer explicit snapshot action
                    query.Set("action", "snapshot");
                    ub.Query = query.ToString();
                    var snapshotUrl = ub.Uri;

                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, snapshotUrl);
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        if (resp.IsSuccessStatusCode)
                        {
                            var ct = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                            if (ct.Contains("jpeg") || ct.Contains("jpg") || ct.Contains("image"))
                            {
                                var bytes = await resp.Content.ReadAsByteArrayAsync();
                                await WriteJpegAsync(ctx, bytes);
                                return;
                            }
                        }
                    }
                    catch { /* fall through to MJPEG parse */ }
                }

                // 2) Fallback: open MJPEG stream and extract first JPEG frame by SOI/EOI markers
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, source);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    resp.EnsureSuccessStatusCode();

                    using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
                    var frame = await ReadSingleJpegFromStreamAsync(s, cts.Token);
                    if (frame != null)
                    {
                        await WriteJpegAsync(ctx, frame);
                        return;
                    }
                }
                catch { /* ignored */ }

                // 3) Final fallback: return black JPEG
                await WriteJpegAsync(ctx, _blackJpeg);
            }
            catch
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 500;
                }
            }
        }

        private static async Task WriteJpegAsync(HttpContext ctx, byte[] bytes)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            ctx.Response.Headers["Pragma"] = "no-cache";
            await ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        }

        private static async Task<byte[]?> ReadSingleJpegFromStreamAsync(Stream stream, CancellationToken ct)
        {
            // Read chunks until we find a full JPEG bounded by SOI (FFD8) and EOI (FFD9)
            var buffer = new byte[64 * 1024];
            using var ms = new MemoryStream();
            int bytesRead;
            var foundSoi = false;
            int prev = -1;
            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    var b = buffer[i];
                    // Check for SOI marker 0xFF 0xD8
                    if (!foundSoi && prev == 0xFF && b == 0xD8)
                    {
                        foundSoi = true;
                        ms.SetLength(0);
                        ms.WriteByte(0xFF);
                        ms.WriteByte(0xD8);
                        prev = -1;
                        continue;
                    }

                    if (foundSoi)
                    {
                        ms.WriteByte(b);
                        // Check for EOI marker 0xFF 0xD9
                        if (prev == 0xFF && b == 0xD9)
                        {
                            return ms.ToArray();
                        }
                    }

                    prev = b;
                }
            }
            return null;
        }
        private async Task ServeBlackFallback(HttpContext ctx, bool probeUpstream = false)
        {
            try
            {
                if (ctx.RequestAborted.IsCancellationRequested) return;
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

                var blackBytes = _blackJpeg;
                var boundary = "--frame\r\n";
                var header = "Content-Type: image/jpeg\r\nContent-Length: ";

                var probeInterval = TimeSpan.FromSeconds(5);
                var lastProbe = DateTime.UtcNow;

                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    try
                    {
                        await ctx.Response.WriteAsync(boundary, ctx.RequestAborted);
                        await ctx.Response.WriteAsync(header + blackBytes.Length + "\r\n\r\n", ctx.RequestAborted);
                        await ctx.Response.Body.WriteAsync(blackBytes, 0, blackBytes.Length, ctx.RequestAborted);
                        await ctx.Response.WriteAsync("\r\n", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception writeEx)
                    {
                        _logger.LogError(writeEx, "[WebCamManager] Fallback MJPEG write failed: {Message}", writeEx.Message);
                        break;
                    }

                    if (probeUpstream && (DateTime.UtcNow - lastProbe) > probeInterval)
                    {
                        lastProbe = DateTime.UtcNow;
                        try
                        {
                            var source = _config.GetValue<string>("Stream:Source");
                            using var probeReq = new HttpRequestMessage(HttpMethod.Get, source);
                            using var probeResp = await _httpClient.SendAsync(probeReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                            if (probeResp.IsSuccessStatusCode)
                            {
                                _logger.LogInformation("[WebCamManager] Upstream source is available again; switching to live feed for client.");
                                ctx.Response.ContentType = probeResp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                                using var upstream = await probeResp.Content.ReadAsStreamAsync(ctx.RequestAborted);
                                await upstream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                                break;
                            }
                        }
                        catch { /* still no upstream; continue fallback */ }
                    }

                    try { await Task.Delay(250, ctx.RequestAborted); } catch (OperationCanceledException) { break; }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WebCamManager] Failed to serve fallback MJPEG: {Message}", ex.Message);
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 502;
                }
            }
        }
    }
}
