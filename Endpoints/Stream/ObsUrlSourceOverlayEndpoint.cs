using FastEndpoints;
using PrintStreamer.Overlay;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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
                var d = await _overlayText.GetOverlayDataAsync(ct);

                var response = new ObsUrlSourceOverlayData
                {
                    Nozzle = FormatDouble(d.Nozzle, "0"),
                    NozzleTarget = FormatDouble(d.NozzleTarget, "0"),
                    Bed = FormatDouble(d.Bed, "0.0"),
                    BedTarget = FormatDouble(d.BedTarget, "0"),
                    State = d.State ?? string.Empty,
                    Progress = d.Progress.ToString(CultureInfo.InvariantCulture),
                    Layer = FormatNullableInt(d.Layer),
                    LayerMax = FormatNullableInt(d.LayerMax),
                    Time = d.Time.ToString("o", CultureInfo.InvariantCulture),
                    Filename = d.Filename ?? string.Empty,
                    Speed = FormatNullableDouble(d.Speed, "0"),
                    SpeedFactor = FormatNullableDouble(d.SpeedFactor, "0"),
                    Flow = FormatNullableDoubleOrZero(d.Flow, "0.00"),
                    Filament = d.Filament.HasValue ? (d.Filament.Value / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) : string.Empty,
                    FilamentType = d.FilamentType ?? string.Empty,
                    FilamentBrand = d.FilamentBrand ?? string.Empty,
                    FilamentColor = d.FilamentColor ?? string.Empty,
                    FilamentName = d.FilamentName ?? string.Empty,
                    FilamentUsedMm = FormatNullableDouble(d.FilamentUsedMm, "0"),
                    FilamentTotalMm = FormatNullableDouble(d.FilamentTotalMm, "0"),
                    Slicer = d.Slicer ?? string.Empty,
                    Eta = d.ETA ?? string.Empty,
                    AudioName = d.AudioName ?? string.Empty
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

        private static string FormatDouble(double value, string format)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return string.Empty;
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string FormatNullableDouble(double? value, string format)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return string.Empty;
            return value.Value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string FormatNullableDoubleOrZero(double? value, string format)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                return (0.0).ToString(format, CultureInfo.InvariantCulture);
            return value.Value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string FormatNullableInt(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }
    }

    /// <summary>
    /// DTO for OBS URLSource plugin overlay data
    /// </summary>
    public class ObsUrlSourceOverlayData
    {
        [JsonPropertyName("nozzle")]
        public string? Nozzle { get; set; }

        [JsonPropertyName("nozzleTarget")]
        public string? NozzleTarget { get; set; }

        [JsonPropertyName("bed")]
        public string? Bed { get; set; }

        [JsonPropertyName("bedTarget")]
        public string? BedTarget { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("progress")]
        public string? Progress { get; set; }

        [JsonPropertyName("layer")]
        public string? Layer { get; set; }

        [JsonPropertyName("layerMax")]
        public string? LayerMax { get; set; }

        [JsonPropertyName("time")]
        public string? Time { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("speed")]
        public string? Speed { get; set; }

        [JsonPropertyName("speedFactor")]
        public string? SpeedFactor { get; set; }

        [JsonPropertyName("flow")]
        public string? Flow { get; set; }

        [JsonPropertyName("filament")]
        public string? Filament { get; set; }

        [JsonPropertyName("filamentType")]
        public string? FilamentType { get; set; }

        [JsonPropertyName("filamentBrand")]
        public string? FilamentBrand { get; set; }

        [JsonPropertyName("filamentColor")]
        public string? FilamentColor { get; set; }

        [JsonPropertyName("filamentName")]
        public string? FilamentName { get; set; }

        [JsonPropertyName("filamentUsedMm")]
        public string? FilamentUsedMm { get; set; }

        [JsonPropertyName("filamentTotalMm")]
        public string? FilamentTotalMm { get; set; }

        [JsonPropertyName("slicer")]
        public string? Slicer { get; set; }

        [JsonPropertyName("eta")]
        public string? Eta { get; set; }

        [JsonPropertyName("audioName")]
        public string? AudioName { get; set; }
    }
}
