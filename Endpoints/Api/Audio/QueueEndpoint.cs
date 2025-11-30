using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrintStreamer.Services;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class QueueEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Post("/api/audio/queue");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                var names = new List<string>();
                using (var sr = new StreamReader(HttpContext.Request.Body))
                {
                    var body = await sr.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("names", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var el in arr.EnumerateArray())
                                {
                                    if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                                    {
                                        names.Add(el.GetString()!);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                names.AddRange(HttpContext.Request.Query["name"].Where(s => !string.IsNullOrEmpty(s)).Select(s => s!));
                var audio = HttpContext.RequestServices.GetRequiredService<AudioService>();
                audio.Enqueue(names.ToArray());
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(new { success = true, queued = names.Count }, ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
