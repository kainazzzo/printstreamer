using System.Net.Http;
using Microsoft.AspNetCore.Http;

namespace PrintStreamer.Services
{
    public class WebCamManager
    {
        private readonly IConfiguration _config;
        private readonly HttpClient _httpClient;
        private volatile bool _disabled = false;
        // Track active client cancellation tokens so we can cancel upstream copy when disabling
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _activeClients = new System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource>();

        // Fallback black JPEG loaded from file
        private readonly byte[] _blackJpeg;

        public WebCamManager(IConfiguration config)
        {
            _config = config;
            _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            
            // Load fallback_black.jpg from filesystem
            var fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), "fallback_black.jpg");
            if (File.Exists(fallbackPath))
            {
                _blackJpeg = File.ReadAllBytes(fallbackPath);
                Console.WriteLine($"[WebCamManager] Loaded fallback image: {_blackJpeg.Length} bytes");
            }
            else
            {
                // Minimal black JPEG as ultimate fallback if file doesn't exist
                _blackJpeg = Convert.FromBase64String(
                    "/9j/4AAQSkZJRgABAQAAAQABAAD/2wCEAAICAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGBggHBwcHBw0JCQgKCAgJCgsMDAwMDAwMDAwMDAwMDAz/wAALCAABAAEBAREA/8QAFQABAQAAAAAAAAAAAAAAAAAAAAb/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAgP/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwD9AP/Z");
                Console.WriteLine("[WebCamManager] Warning: fallback_black.jpg not found, using hardcoded fallback");
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
            var clientId = Guid.NewGuid();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            _activeClients[clientId] = linkedCts;

            var source = _config.GetValue<string>("Stream:Source");
            Console.WriteLine($"[WebCamManager] Client connected: {ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort}");

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
                Console.WriteLine("[WebCamManager] Client disconnected or request canceled.");
                // If we were canceled because the manager disabled the webcam, serve fallback instead
                if (_disabled && !ctx.RequestAborted.IsCancellationRequested)
                {
                    // If we were canceled due to disabling the manager, ensure we serve the black fallback and do not probe upstream.
                    try { await ServeBlackFallback(ctx, probeUpstream: false); } catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebCamManager] Upstream error: {ex.Message} - serving fallback MJPEG");
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
                        Console.WriteLine($"[WebCamManager] Fallback MJPEG write failed: {writeEx.Message}");
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
                                Console.WriteLine("[WebCamManager] Upstream source is available again; switching to live feed for client.");
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
                Console.WriteLine($"[WebCamManager] Failed to serve fallback MJPEG: {ex.Message}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 502;
                }
            }
        }
    }
}
