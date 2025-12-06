using FastEndpoints;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PrintStreamer.Overlay;

namespace PrintStreamer.Endpoints.Stream
{
    public class ObsUrlSourceOverlayEndpoint : EndpointWithoutRequest<ObsUrlSourceOverlayData>
    {
        private readonly OverlayTextService _overlayText;

        public ObsUrlSourceOverlayEndpoint(OverlayTextService overlayText)
        {
            _overlayText = overlayText;
        }

        public override void Configure()
        {
            Get("/stream/obs-urlsource/overlay");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            try
            {
                // Get the current overlay data
                var overlayData = await _overlayText.GetOverlayDataAsync(ct);
                
                // Convert dynamic to the strongly-typed response object
                dynamic d = overlayData;
                var response = new ObsUrlSourceOverlayData
                {
                    Nozzle = d.Nozzle,
                    NozzleTarget = d.NozzleTarget,
                    Bed = d.Bed,
                    BedTarget = d.BedTarget,
                    State = d.State,
                    Progress = d.Progress,
                    Layer = d.Layer,
                    LayerMax = d.LayerMax,
                    Time = d.Time,
                    Filename = d.Filename,
                    Speed = d.Speed,
                    SpeedFactor = d.SpeedFactor,
                    Flow = d.Flow,
                    Filament = d.Filament,
                    FilamentType = d.FilamentType,
                    FilamentBrand = d.FilamentBrand,
                    FilamentColor = d.FilamentColor,
                    FilamentName = d.FilamentName,
                    FilamentUsedMm = d.FilamentUsedMm,
                    FilamentTotalMm = d.FilamentTotalMm,
                    Slicer = d.Slicer,
                    Eta = d.ETA,
                    AudioName = d.AudioName
                };
                
                // Use ASP.NET Core's built-in JSON serialization
                HttpContext.Response.StatusCode = 200;
                await HttpContext.Response.WriteAsJsonAsync(response, ct);
            }
            catch (System.Exception ex)
            {
                HttpContext.Response.StatusCode = 500;
                await HttpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
            }
        }
    }

    /// <summary>
    /// DTO for OBS URLSource plugin overlay data
    /// </summary>
    public class ObsUrlSourceOverlayData
    {
        [JsonPropertyName("nozzle")]
        public double Nozzle { get; set; }

        [JsonPropertyName("nozzleTarget")]
        public double NozzleTarget { get; set; }

        [JsonPropertyName("bed")]
        public double Bed { get; set; }

        [JsonPropertyName("bedTarget")]
        public double BedTarget { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("progress")]
        public int Progress { get; set; }

        [JsonPropertyName("layer")]
        public int? Layer { get; set; }

        [JsonPropertyName("layerMax")]
        public int? LayerMax { get; set; }

        [JsonPropertyName("time")]
        public System.DateTime Time { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("speed")]
        public double? Speed { get; set; }

        [JsonPropertyName("speedFactor")]
        public double? SpeedFactor { get; set; }

        [JsonPropertyName("flow")]
        public double? Flow { get; set; }

        [JsonPropertyName("filament")]
        public double? Filament { get; set; }

        [JsonPropertyName("filamentType")]
        public string? FilamentType { get; set; }

        [JsonPropertyName("filamentBrand")]
        public string? FilamentBrand { get; set; }

        [JsonPropertyName("filamentColor")]
        public string? FilamentColor { get; set; }

        [JsonPropertyName("filamentName")]
        public string? FilamentName { get; set; }

        [JsonPropertyName("filamentUsedMm")]
        public double? FilamentUsedMm { get; set; }

        [JsonPropertyName("filamentTotalMm")]
        public double? FilamentTotalMm { get; set; }

        [JsonPropertyName("slicer")]
        public string? Slicer { get; set; }

        [JsonPropertyName("eta")]
        public System.DateTime? Eta { get; set; }

        [JsonPropertyName("audioName")]
        public string? AudioName { get; set; }
    }
}
