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
    public class RemoveFromQueueEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Post("/api/audio/queue/remove");
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

                if (names.Count == 0)
                {
                    HttpContext.Response.StatusCode = 400;
                    await HttpContext.Response.WriteAsJsonAsync(new { error = "Missing 'name'" }, ct);
                    return;
                }

                var audio = HttpContext.RequestServices.GetRequiredService<AudioService>();
                var removed = audio.RemoveFromQueue(names.ToArray());
                await HttpContext.Response.WriteAsJsonAsync(new { success = true, removed }, ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
