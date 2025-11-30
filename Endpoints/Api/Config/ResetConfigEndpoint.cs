using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace PrintStreamer.Endpoints.Api.Config
{
    public class ResetConfigEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure() { Post("/api/config/reset"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            var logger = ctx.RequestServices.GetRequiredService<ILogger<ResetConfigEndpoint>>();
            var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
            try
            {
                var defaultConfig = new
                {
                    Stream = new { Source = "http://192.168.1.2/webcam", Audio = new { UseApiStream = true, Url = "http://127.0.0.1:8080/api/audio/stream" } },
                    Audio = new { Folder = "audio", Enabled = true },
                    Moonraker = new { BaseUrl = "http://192.168.1.2:7125/", ApiKey = "", AuthHeader = "X-Api-Key" },
                    Overlay = new { Enabled = false, RefreshMs = 500, Template = "Nozzle: {nozzle:0}°C/{nozzleTarget:0}°C | Bed: {bed:0}°C/{bedTarget:0}°C | Layer {layers} | {progress:0}%\nSpeed:{speed}mm/s | Flow:{flow} | Fil:{filament}m | ETA:{eta:hh:mm tt}", FontFile = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", FontSize = 16, FontColor = "white", Box = true, BoxColor = "black@0.4", BoxBorderW = 8, X = "(w-tw)-20", Y = "", BannerFraction = 0.2, ShowFilamentInOverlay = true, FilamentCacheSeconds = 60 },
                    YouTube = new { OAuth = new { ClientId = "", ClientSecret = "" }, LiveBroadcast = new { Title = "3D Printer Live Stream", Description = "Live stream from my 3D printer.", Privacy = "unlisted", CategoryId = "28", Enabled = true }, Playlist = new { Name = "PrintStreamer", Privacy = "unlisted" } },
                    Timelapse = new { MainFolder = "timelapse", Period = "00:01:00", Upload = true }
                };

                var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = null };
                var jsonString = System.Text.Json.JsonSerializer.Serialize(defaultConfig, options);
                await File.WriteAllTextAsync(appSettingsPath, jsonString);
                logger.LogInformation("Configuration reset to defaults");
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsJsonAsync(new { success = true, message = "Configuration reset to defaults" }, ct);
            }
            catch (System.Exception ex)
            {
                logger.LogError(ex, "Error resetting configuration: {Message}", ex.Message);
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
