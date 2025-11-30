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
        private readonly IConfiguration _config;
        public GetConfigEndpoint(IConfiguration config)
        {
            _config = config;
        }

        public override void Configure()
        {
            Get("/api/config");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var currentConfig = new
            {
                Stream = new
                {
                    Source = _config.GetValue<string>("Stream:Source"),
                    TargetFps = _config.GetValue<int?>("Stream:TargetFps") ?? 6,
                    BitrateKbps = _config.GetValue<int?>("Stream:BitrateKbps") ?? 800,
                    Local = new { Enabled = _config.GetValue<bool?>("Stream:Local:Enabled") ?? false },
                    Audio = new { UseApiStream = _config.GetValue<bool?>("Stream:Audio:UseApiStream") ?? true, Url = _config.GetValue<string>("Stream:Audio:Url") ?? "http://127.0.0.1:8080/api/audio/stream" }
                },
                Audio = new { Folder = _config.GetValue<string>("Audio:Folder") ?? "audio", Enabled = _config.GetValue<bool?>("Audio:Enabled") ?? true },
                Moonraker = new { BaseUrl = _config.GetValue<string>("Moonraker:BaseUrl"), ApiKey = _config.GetValue<string>("Moonraker:ApiKey"), AuthHeader = _config.GetValue<string>("Moonraker:AuthHeader") ?? "X-Api-Key" },
                Overlay = new { Enabled = _config.GetValue<bool?>("Overlay:Enabled") ?? false, RefreshMs = _config.GetValue<int?>("Overlay:RefreshMs") ?? 500, Template = _config.GetValue<string>("Overlay:Template"), FontFile = _config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", FontSize = _config.GetValue<int?>("Overlay:FontSize") ?? 16, FontColor = _config.GetValue<string>("Overlay:FontColor") ?? "white", Box = _config.GetValue<bool?>("Overlay:Box") ?? true, BoxColor = _config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4", BoxBorderW = _config.GetValue<int?>("Overlay:BoxBorderW") ?? 2, X = _config.GetValue<string>("Overlay:X") ?? "0", Y = _config.GetValue<string>("Overlay:Y") ?? "40", BannerFraction = _config.GetValue<double?>("Overlay:BannerFraction") ?? 0.2, ShowFilamentInOverlay = _config.GetValue<bool?>("Overlay:ShowFilamentInOverlay") ?? true, FilamentCacheSeconds = _config.GetValue<int?>("Overlay:FilamentCacheSeconds") ?? 60 },
                YouTube = new { OAuth = new { ClientId = _config.GetValue<string>("YouTube:OAuth:ClientId"), ClientSecret = _config.GetValue<string>("YouTube:OAuth:ClientSecret") }, LiveBroadcast = new { Title = _config.GetValue<string>("YouTube:LiveBroadcast:Title") ?? "3D Printer Live Stream", Description = _config.GetValue<string>("YouTube:LiveBroadcast:Description") ?? "Live stream from my 3D printer.", Privacy = _config.GetValue<string>("YouTube:LiveBroadcast:Privacy") ?? "unlisted", CategoryId = _config.GetValue<string>("YouTube:LiveBroadcast:CategoryId") ?? "28", Enabled = _config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true }, Playlist = new { Name = _config.GetValue<string>("YouTube:Playlist:Name") ?? "PrintStreamer", Privacy = _config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted" }, TimelapseUpload = new { Enabled = _config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false, Privacy = _config.GetValue<string>("YouTube:TimelapseUpload:Privacy") ?? "public", CategoryId = _config.GetValue<string>("YouTube:TimelapseUpload:CategoryId") ?? "28" } },
                Timelapse = new { MainFolder = _config.GetValue<string>("Timelapse:MainFolder") ?? "timelapse", Period = _config.GetValue<string>("Timelapse:Period") ?? "00:01:00", LastLayerOffset = _config.GetValue<int?>("Timelapse:LastLayerOffset") ?? 1 },
                Serve = new { Enabled = _config.GetValue<bool?>("Serve:Enabled") ?? true },
                PrinterUI = new { MainsailUrl = _config.GetValue<string>("PrinterUI:MainsailUrl") ?? "http://192.168.1.117/mainsail", FluiddUrl = _config.GetValue<string>("PrinterUI:FluiddUrl") ?? "http://192.168.1.117/fluid" }
            };
            HttpContext.Response.StatusCode = 200;
            await HttpContext.Response.WriteAsJsonAsync(currentConfig, ct);
        }
    }
}
