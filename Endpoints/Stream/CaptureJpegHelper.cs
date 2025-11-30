using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System;
using System.Net.Http;

namespace PrintStreamer.Endpoints.Stream
{
    public static class CaptureJpegHelper
    {
        public static async Task<bool> TryCaptureJpegFromStreamAsync(HttpContext ctx, string streamUrl, string name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync($"{name} source not available");
                return false;
            }

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            try
            {
                if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var srcUri))
                {
                    var ub = new UriBuilder(srcUri);
                    var query = System.Web.HttpUtility.ParseQueryString(ub.Query);
                    query.Set("action", "snapshot");
                    ub.Query = query.ToString();

                    try
                    {
                        using var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, ub.Uri);
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        using var resp = await httpClient.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        if (resp.IsSuccessStatusCode)
                        {
                            var ctHeader = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                            if (ctHeader.Contains("jpeg") || ctHeader.Contains("jpg") || ctHeader.Contains("image"))
                            {
                                var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                                ctx.Response.StatusCode = 200;
                                ctx.Response.ContentType = "image/jpeg";
                                ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                                await ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, ctx.RequestAborted);
                                return true;
                            }
                        }
                    }
                    catch { /* fall through to MJPEG parse */ }
                }

                // Fallback: parse JPEG from MJPEG stream
                using (var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, streamUrl))
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
                using (var resp = await httpClient.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    resp.EnsureSuccessStatusCode();
                    using var s = await resp.Content.ReadAsStreamAsync(cts.Token);

                    var buffer = new byte[64 * 1024];
                    using var ms = new MemoryStream();
                    int bytesRead;
                    bool foundSoi = false;
                    int prev = -1;

                    while ((bytesRead = await s.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
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
                                    var frameBytes = ms.ToArray();
                                    ctx.Response.StatusCode = 200;
                                    ctx.Response.ContentType = "image/jpeg";
                                    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                                    await ctx.Response.Body.WriteAsync(frameBytes, 0, frameBytes.Length, ct);
                                    return true;
                                }
                            }

                            prev = b;
                        }
                    }
                }

                // Failed to get a frame
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync($"Failed to capture frame from {name}");
                return false;
            }
            catch (TimeoutException)
            {
                ctx.Response.StatusCode = 504;
                await ctx.Response.WriteAsync("Capture timeout");
                return false;
            }
            catch (OperationCanceledException)
            {
                // downstream canceled
                return false;
            }
            catch (Exception ex)
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 502;
                    await ctx.Response.WriteAsync("Capture error: " + ex.Message);
                }
                return false;
            }
        }
    }
}
