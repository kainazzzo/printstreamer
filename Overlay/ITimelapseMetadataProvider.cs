using System.Text.Json.Nodes;

namespace PrintStreamer.Overlay
{
    public interface ITimelapseMetadataProvider
    {
        /// <summary>
        /// Return metadata for an active timelapse session matching the given filename (printer-reported filename).
        /// Returns null if no active session or no cached metadata.
        /// </summary>
        TimelapseSessionMetadata? GetMetadataForFilename(string filename);
    }

    public sealed class TimelapseSessionMetadata
    {
        public int? TotalLayersFromMetadata { get; init; }
        public JsonNode? RawMetadata { get; init; }
        public string? Slicer { get; init; }
        public double? EstimatedSeconds { get; init; }
        // Slicer settings for volumetric flow calculation
        public double? LayerHeight { get; init; }
        public double? FirstLayerHeight { get; init; }
        public double? ExtrusionWidth { get; init; }
        // File-level filament totals (mm) parsed from metadata
        public double? FilamentTotalMm { get; init; }
    }
}
