using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace PrintStreamer.Endpoints.Api.Config
{
    public class GetConfigEndpoint : EndpointWithoutRequest<object>
    {
        public override void Configure()
        {
            Get("/api/config");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var currentConfig = new
            {
                Stream = new
                {
                    Source = config.GetValue<string>("Stream:Source"),
                    TargetFps = config.GetValue<int?>("Stream:TargetFps") ?? 6,
                    BitrateKbps = config.GetValue<int?>("Stream:BitrateKbps") ?? 800,
                    Local = new { Enabled = config.GetValue<bool?>("Stream:Local:Enabled") ?? false },
                    Audio = new { UseApiStream = config.GetValue<bool?>("Stream:Audio:UseApiStream") ?? true, Url = config.GetValue<string>("Stream:Audio:Url") ?? "http://127.0.0.1:8080/api/audio/stream" }
                },
                Audio = new { Folder = config.GetValue<string>("Audio:Folder") ?? "audio", Enabled = config.GetValue<bool?>("Audio:Enabled") ?? true },
                Moonraker = new { BaseUrl = config.GetValue<string>("Moonraker:BaseUrl"), ApiKey = config.GetValue<string>("Moonraker:ApiKey"), AuthHeader = config.GetValue<string>("Moonraker:AuthHeader") ?? "X-Api-Key" },
                Overlay = new { Enabled = config.GetValue<bool?>("Overlay:Enabled") ?? false, RefreshMs = config.GetValue<int?>("Overlay:RefreshMs") ?? 500, Template = config.GetValue<string>("Overlay:Template"), FontFile = config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", FontSize = config.GetValue<int?>("Overlay:FontSize") ?? 16, FontColor = config.GetValue<string>("Overlay:FontColor") ?? "white", Box = config.GetValue<bool?>("Overlay:Box") ?? true, BoxColor = config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4", BoxBorderW = config.GetValue<int?>("Overlay:BoxBorderW") ?? 2, X = config.GetValue<string>("Overlay:X") ?? "0", Y = config.GetValue<string>("Overlay:Y") ?? "40", BannerFraction = config.GetValue<double?>("Overlay:BannerFraction") ?? 0.2, ShowFilamentInOverlay = config.GetValue<bool?>("Overlay:ShowFilamentInOverlay") ?? true, FilamentCacheSeconds = config.GetValue<int?>("Overlay:FilamentCacheSeconds") ?? 60 },
                YouTube = new { OAuth = new { ClientId = config.GetValue<string>("YouTube:OAuth:ClientId"), ClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret") }, LiveBroadcast = new { Title = config.GetValue<string>("YouTube:LiveBroadcast:Title") ?? "3D Printer Live Stream", Description = config.GetValue<string>("YouTube:LiveBroadcast:Description") ?? "Live stream from my 3D printer.", Privacy = config.GetValue<string>("YouTube:LiveBroadcast:Privacy") ?? "unlisted", CategoryId = config.GetValue<string>("YouTube:LiveBroadcast:CategoryId") ?? "28", Enabled = config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true }, Playlist = new { Name = config.GetValue<string>("YouTube:Playlist:Name") ?? "PrintStreamer", Privacy = config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted" }, TimelapseUpload = new { Enabled = config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false, Privacy = config.GetValue<string>("YouTube:TimelapseUpload:Privacy") ?? "public", CategoryId = config.GetValue<string>("YouTube:TimelapseUpload:CategoryId") ?? "28" } },
                Timelapse = new { MainFolder = config.GetValue<string>("Timelapse:MainFolder") ?? "timelapse", Period = config.GetValue<string>("Timelapse:Period") ?? "00:01:00", LastLayerOffset = config.GetValue<int?>("Timelapse:LastLayerOffset") ?? 1 },
                Serve = new { Enabled = config.GetValue<bool?>("Serve:Enabled") ?? true },
                PrinterUI = new { MainsailUrl = config.GetValue<string>("PrinterUI:MainsailUrl") ?? "http://192.168.1.117/mainsail", FluiddUrl = config.GetValue<string>("PrinterUI:FluiddUrl") ?? "http://192.168.1.117/fluid" }
            };
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(currentConfig, ct);
        }
    }
}
