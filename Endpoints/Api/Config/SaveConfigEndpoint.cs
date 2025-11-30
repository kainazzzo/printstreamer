using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace PrintStreamer.Endpoints.Api.Config
{
    public class SaveConfigEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Post("/api/config"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            var logger = ctx.RequestServices.GetRequiredService<ILogger<SaveConfigEndpoint>>();
            var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body)) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { success = false, error = "Invalid configuration data" }, ct); return; }
                try { System.Text.Json.JsonDocument.Parse(body); }
                catch (System.Text.Json.JsonException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { success = false, error = "Invalid JSON format" }, ct); return; }

                var customConfigFile = config.GetValue<string>("CustomConfigFile") ?? "appsettings.Local.json";
                var configPath = Path.Combine(Directory.GetCurrentDirectory(), customConfigFile);
                logger.LogInformation("Saving configuration to: {ConfigFile}", customConfigFile);

                var jsonDoc = System.Text.Json.JsonDocument.Parse(body);
                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = null };
                var jsonString = System.Text.Json.JsonSerializer.Serialize(jsonDoc.RootElement, options);
                await File.WriteAllTextAsync(configPath, jsonString);
                logger.LogInformation("Configuration saved to {ConfigFile}", customConfigFile);
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsJsonAsync(new { success = true, message = "Configuration saved. Restart required for changes to take effect." }, ct);
            }
            catch (System.Exception ex)
            {
                logger.LogError(ex, "Error saving configuration: {Message}", ex.Message);
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
