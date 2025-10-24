// Minimal Moonraker client helpers
using System.Text.Json.Nodes;

internal static class MoonrakerClient
{
    // Minimal MoonrakerPrintInfo used by Program.cs and other callers
    public class MoonrakerPrintInfo
    {
        public string? Filename { get; set; }
        public string? JobQueueId { get; set; }
        public string? State { get; set; }
        public TimeSpan? Remaining { get; set; }
        public double? ProgressPercent { get; set; }
        public int? CurrentLayer { get; set; }
        public int? TotalLayers { get; set; }

        // Additional fields used by callers (best-effort; may be null)
        public TimeSpan? Elapsed { get; set; }

        // Bed temperatures
        public double? BedTempActual { get; set; }
        public double? BedTempTarget { get; set; }

        // Tool temperature for tool 0 (nullable struct to match usage patterns)
        public ToolTemp? Tool0Temp { get; set; }

        // Filament info (if available)
        public string? FilamentType { get; set; }
        public string? FilamentColor { get; set; }
        public string? FilamentBrand { get; set; }
        public double? FilamentUsedMm { get; set; }
        public double? FilamentTotalMm { get; set; }

        // Sensors and their measurements (friendly name + measurements map)
        public System.Collections.Generic.List<SensorInfo>? Sensors { get; set; }

        public struct ToolTemp
        {
            public double? Actual { get; set; }
            public double? Target { get; set; }
        }

        public class SensorInfo
        {
            public string? Name { get; set; }
            public string? FriendlyName { get; set; }
            public System.Collections.Generic.Dictionary<string, object?> Measurements { get; set; } = new System.Collections.Generic.Dictionary<string, object?>();
        }
    }

    /// <summary>
    /// Best-effort: fetch a small summary of the current print job. Implemented as a stub that
    /// attempts common Moonraker endpoints. Returns null when information cannot be retrieved.
    /// </summary>
    public static async Task<MoonrakerPrintInfo?> GetPrintInfoAsync(Uri baseUri, string? apiKey = null, string? authHeader = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var header = string.IsNullOrWhiteSpace(authHeader) ? "X-Api-Key" : authHeader;
                try { http.DefaultRequestHeaders.Remove(header); } catch { }
                try { http.DefaultRequestHeaders.Add(header, apiKey); } catch { }
            }

            // 1) Prefer print_stats which can include current/total layer when the slicer emits SET_PRINT_STATS_INFO
            try
            {
                var resp = await http.GetAsync("/printer/objects/query?print_stats", cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    var txt = await resp.Content.ReadAsStringAsync(cancellationToken);
                    var root = JsonNode.Parse(txt);
                    var status = root?["result"]?["status"]?["print_stats"] as JsonObject;
                    if (status != null)
                    {
                        var info = new MoonrakerPrintInfo();
                        // filename/state/progress
                        info.Filename = status["filename"]?.ToString();
                        info.State = status["state"]?.ToString();
                        // Progress may be 0..1 or 0..100 depending on frontend; normalize to 0..100
                        double? progRaw = TryGetDouble(status["progress"]);
                        if (progRaw.HasValue)
                        {
                            info.ProgressPercent = progRaw.Value <= 1.0 ? progRaw.Value * 100.0 : progRaw.Value;
                        }

                        // info object can contain slicer-provided fields
                        var infoObj = status["info"] as JsonObject;
                        if (infoObj != null)
                        {
                            // Common keys observed across slicers/frontends
                            info.CurrentLayer = TryGetInt(
                                infoObj["CURRENT_LAYER"] ?? infoObj["current_layer"] ?? infoObj["layer"] ?? infoObj["currentLayer"]);
                            info.TotalLayers = TryGetInt(
                                infoObj["TOTAL_LAYER"] ?? infoObj["total_layer"] ?? infoObj["total_layers"] ?? infoObj["total_layer_count"] ?? infoObj["layer_count"]);

                            // Remaining time (best-effort). Often reported in seconds.
                            var remainingSec = TryGetDouble(infoObj["time_remaining"] ?? infoObj["remaining_time"] ?? infoObj["eta_seconds"]);
                            if (remainingSec.HasValue)
                            {
                                try { info.Remaining = TimeSpan.FromSeconds(remainingSec.Value); } catch { }
                            }

                            // Filament info extraction (common keys: filament_type, filament_color, filament_brand, filament_used_mm, filament_total_mm)
                            info.FilamentType = infoObj["filament_type"]?.ToString() ?? infoObj["FILAMENT_TYPE"]?.ToString();
                            info.FilamentColor = infoObj["filament_color"]?.ToString() ?? infoObj["FILAMENT_COLOR"]?.ToString();
                            info.FilamentBrand = infoObj["filament_brand"]?.ToString() ?? infoObj["FILAMENT_BRAND"]?.ToString();
                            info.FilamentUsedMm = TryGetDouble(infoObj["filament_used_mm"] ?? infoObj["FILAMENT_USED_MM"]);
                            info.FilamentTotalMm = TryGetDouble(infoObj["filament_total_mm"] ?? infoObj["FILAMENT_TOTAL_MM"]);
                        }

                        // Fallback: if progress percent not provided but layers are, compute approx
                        if (!info.ProgressPercent.HasValue && info.CurrentLayer.HasValue && info.TotalLayers.HasValue && info.TotalLayers.Value > 0)
                        {
                            info.ProgressPercent = (double)info.CurrentLayer.Value / (double)info.TotalLayers.Value * 100.0;
                        }

                        return info;
                    }
                }
            }
            catch { }

            // 2) Fallbacks: /printer/print and display_status for minimal info
            var candidates = new[] { "/printer/print", "/printer/objects/query?display_status" };
            foreach (var ep in candidates)
            {
                try
                {
                    var resp = await http.GetAsync(ep, cancellationToken);
                    if (!resp.IsSuccessStatusCode) continue;
                    var txt = await resp.Content.ReadAsStringAsync(cancellationToken);
                    var node = JsonNode.Parse(txt);
                    if (node == null) continue;
                    var info = new MoonrakerPrintInfo();

                    // Heuristic pulls
                    info.Filename = FindStringEndingWith(node, ".gcode") ?? info.Filename;
                    info.State = FindFirstOf(node, new[] { "printing", "paused", "complete", "error", "idle" }) ?? info.State;
                    var p = TryFindDouble(node, new[] { "progress" });
                    if (p.HasValue) info.ProgressPercent = p.Value <= 1.0 ? p.Value * 100.0 : p.Value;
                    // No layers in these endpoints typically
                    return info;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static int? TryGetInt(JsonNode? n)
    {
        try
        {
            if (n == null) return null;
            if (n is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                if (int.TryParse(v.ToString(), out var i2)) return i2;
            }
        }
        catch { }
        return null;
    }

    private static double? TryGetDouble(JsonNode? n)
    {
        try
        {
            if (n == null) return null;
            if (n is JsonValue v)
            {
                if (v.TryGetValue<double>(out var d)) return d;
                if (double.TryParse(v.ToString(), out var d2)) return d2;
            }
        }
        catch { }
        return null;
    }

    private static string? FindStringEndingWith(JsonNode n, string suffix)
    {
        try
        {
            string? found = null;
            void Walk(JsonNode? node)
            {
                if (node == null || found != null) return;
                if (node is JsonValue v)
                {
                    var s = v.ToString();
                    if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { found = s; return; }
                }
                else if (node is JsonObject o)
                {
                    foreach (var kv in o) { Walk(kv.Value); if (found != null) break; }
                }
                else if (node is JsonArray a)
                {
                    foreach (var it in a) { Walk(it); if (found != null) break; }
                }
            }
            Walk(n);
            return found;
        }
        catch { return null; }
    }

    private static string? FindFirstOf(JsonNode n, IEnumerable<string> candidates)
    {
        try
        {
            string? found = null;
            var set = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
            void Walk(JsonNode? node)
            {
                if (node == null || found != null) return;
                if (node is JsonValue v)
                {
                    var s = v.ToString();
                    if (set.Contains(s)) { found = s; return; }
                }
                else if (node is JsonObject o)
                {
                    foreach (var kv in o) { Walk(kv.Value); if (found != null) break; }
                }
                else if (node is JsonArray a)
                {
                    foreach (var it in a) { Walk(it); if (found != null) break; }
                }
            }
            Walk(n);
            return found;
        }
        catch { return null; }
    }

    private static double? TryFindDouble(JsonNode n, IEnumerable<string> candidateKeys)
    {
        // Search the JSON object graph for the first numeric value under any of the given keys
        try
        {
            double? found = null;
            var set = new HashSet<string>(candidateKeys, StringComparer.OrdinalIgnoreCase);
            void Walk(string? key, JsonNode? node)
            {
                if (node == null || found.HasValue) return;
                if (node is JsonValue v)
                {
                    if (key != null && set.Contains(key))
                    {
                        if (v.TryGetValue<double>(out var d)) { found = d; return; }
                        if (double.TryParse(v.ToString(), out var d2)) { found = d2; return; }
                    }
                }
                else if (node is JsonObject o)
                {
                    foreach (var kv in o)
                    {
                        Walk(kv.Key, kv.Value);
                        if (found.HasValue) break;
                    }
                }
                else if (node is JsonArray a)
                {
                    foreach (var it in a)
                    {
                        Walk(null, it);
                        if (found.HasValue) break;
                    }
                }
            }
            Walk(null, n);
            return found;
        }
        catch { return null; }
    }

    /// <summary>
    /// Try to extract a base printer URI (scheme + host) from the configured Stream:Source URL.
    /// Returns a Uri pointing at the printer host with port 7125 (Moonraker default) when possible.
    /// </summary>
    public static Uri? GetPrinterBaseUriFromStreamSource(string source)
    {
        try
        {
            // If source is a full URL, parse it and replace the port with 7125
            if (Uri.TryCreate(source, UriKind.Absolute, out var srcUri))
            {
                var builder = new UriBuilder(srcUri)
                {
                    Port = 7125,
                    Path = string.Empty,
                    Query = string.Empty
                };
                return builder.Uri;
            }

            // Fallback: try to interpret as host or host:port
            if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var host = source.Split('/')[0];
                if (!host.Contains(":")) host = host + ":7125";
                if (Uri.TryCreate("http://" + host, UriKind.Absolute, out var u)) return u;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Fetch file metadata for a given filename from Moonraker: /server/files/metadata?filename=...
    /// Returns the parsed JsonNode (raw response) or null on failure.
    /// </summary>
    public static async Task<JsonNode?> GetFileMetadataAsync(Uri baseUri, string filename, string? apiKey = null, string? authHeader = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var header = string.IsNullOrWhiteSpace(authHeader) ? "X-Api-Key" : authHeader;
                try { http.DefaultRequestHeaders.Remove(header); } catch { }
                try { http.DefaultRequestHeaders.Add(header, apiKey); } catch { }
            }

            // Try the filename as provided first
            var endpoints = new List<string> { $"/server/files/metadata?filename={Uri.EscapeDataString(filename)}" };
            // If the filename looks like a bare name, also try "gcodes/" prefix (best-effort)
            if (!string.IsNullOrWhiteSpace(filename) && !filename.Contains('/') && !filename.StartsWith("gcodes/", StringComparison.OrdinalIgnoreCase))
            {
                endpoints.Add($"/server/files/metadata?filename={Uri.EscapeDataString("gcodes/" + filename)}");
            }

            foreach (var endpoint in endpoints)
            {
                Console.WriteLine($"[Moonraker] GET {endpoint}");
                var resp = await http.GetAsync(endpoint, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    var txt = await resp.Content.ReadAsStringAsync(cancellationToken);
                    return JsonNode.Parse(txt);
                }
                else
                {
                    Console.WriteLine($"[Moonraker] Metadata request failed for '{endpoint}': {(int)resp.StatusCode} {resp.ReasonPhrase}");
                }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Note: Download/list helpers were removed. The project no longer attempts to download or list
    /// G-code files from Moonraker. GetFileMetadataAsync will still try a sensible filename and a
    /// "gcodes/" prefixed variant when appropriate.
    /// </summary>

    /// <summary>
    /// List files on the Moonraker server under an optional path (for example 'gcodes/').
    /// Returns the parsed JsonNode or null on failure. This is a best-effort helper and
    /// callers should handle different response shapes from various frontends.
    /// </summary>
    // ListFilesAsync removed — the client no longer exposes listing of server-side files.

    /// <summary>
    /// Fetch the printer print_stats object via /printer/objects/query?print_stats
    /// Returns the parsed JsonNode or null on failure.
    /// </summary>
    public static async Task<JsonNode?> GetPrintStatsAsync(Uri baseUri, string? apiKey = null, string? authHeader = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var header = string.IsNullOrWhiteSpace(authHeader) ? "X-Api-Key" : authHeader;
                try { http.DefaultRequestHeaders.Remove(header); } catch { }
                try { http.DefaultRequestHeaders.Add(header, apiKey); } catch { }
            }

            var endpoint = "/printer/objects/query?print_stats";
            Console.WriteLine($"[Moonraker] GET {endpoint}");
            var resp = await http.GetAsync(endpoint, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                var txt = await resp.Content.ReadAsStringAsync(cancellationToken);
                return JsonNode.Parse(txt);
            }
            else
            {
                Console.WriteLine($"[Moonraker] print_stats request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Moonraker] GetPrintStatsAsync error: {ex.Message}");
        }
        return null;
    }

    // ... rest of MoonrakerClient methods remain unchanged (GetPrintInfoAsync, etc.)
}
