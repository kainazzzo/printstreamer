// Minimal Moonraker client helpers
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

public class MoonrakerClient
{
    private readonly ILogger<MoonrakerClient> _logger;

    public MoonrakerClient(ILogger<MoonrakerClient> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
        public List<SensorInfo>? Sensors { get; set; }

        public struct ToolTemp
        {
            public double? Actual { get; set; }
            public double? Target { get; set; }
        }

        public class SensorInfo
        {
            public string? Name { get; set; }
            public string? FriendlyName { get; set; }
            public Dictionary<string, object?> Measurements { get; set; } = new Dictionary<string, object?>();
        }
    }

    /// <summary>
    /// Best-effort: fetch a small summary of the current print job. Implemented as a stub that
    /// attempts common Moonraker endpoints. Returns null when information cannot be retrieved.
    /// </summary>
    public async Task<MoonrakerPrintInfo?> GetPrintInfoAsync(Uri baseUri, string? apiKey = null, string? authHeader = null, CancellationToken cancellationToken = default)
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

                        // If any filament fields are missing and we have a filename, try fetching file metadata as a fallback
                        if (!string.IsNullOrWhiteSpace(info.Filename) &&
                            (string.IsNullOrWhiteSpace(info.FilamentType) ||
                             string.IsNullOrWhiteSpace(info.FilamentBrand) ||
                             string.IsNullOrWhiteSpace(info.FilamentColor) ||
                             !info.FilamentUsedMm.HasValue ||
                             !info.FilamentTotalMm.HasValue))
                        {
                            await MergeFilamentFromMetadataAsync(info, baseUri, apiKey, authHeader, cancellationToken);
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

    /// <summary>
    /// Helper to merge filament metadata from file metadata endpoint into MoonrakerPrintInfo.
    /// Only updates fields that are currently missing (null/empty).
    /// </summary>
    private async Task MergeFilamentFromMetadataAsync(MoonrakerPrintInfo info, Uri baseUri, string? apiKey, string? authHeader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(info.Filename)) return;

        try
        {
            var metadataNode = await this.GetFileMetadataAsync(baseUri, info.Filename, apiKey, authHeader, cancellationToken);
            if (metadataNode == null) return;

            // Navigate to result object (typical structure: { "result": { ... } })
            var result = metadataNode["result"] as JsonObject;
            if (result == null) return;

            // Extract filament fields with normalization (case-insensitive, handle variants)
            if (string.IsNullOrWhiteSpace(info.FilamentType))
            {
                info.FilamentType = GetFilamentString(result, "filament_type");
            }
            if (string.IsNullOrWhiteSpace(info.FilamentBrand))
            {
                info.FilamentBrand = GetFilamentString(result, "filament_name") ?? GetFilamentString(result, "filament_brand");
            }
            if (string.IsNullOrWhiteSpace(info.FilamentColor))
            {
                info.FilamentColor = GetFilamentString(result, "filament_color");
            }
            if (!info.FilamentUsedMm.HasValue)
            {
                // Try: filament_used_mm, filament_used (assume mm), filament_used_m (convert to mm)
                info.FilamentUsedMm = GetFilamentDouble(result, "filament_used_mm") ??
                                      GetFilamentDouble(result, "filament_used") ??
                                      (GetFilamentDouble(result, "filament_used_m") * 1000.0);
            }
            if (!info.FilamentTotalMm.HasValue)
            {
                // Try: filament_total_mm, filament_total (assume mm), filament_total_m (convert to mm)
                info.FilamentTotalMm = GetFilamentDouble(result, "filament_total_mm") ??
                                       GetFilamentDouble(result, "filament_total") ??
                                       (GetFilamentDouble(result, "filament_total_m") * 1000.0);
            }
        }
        catch
        {
            _logger.LogWarning("[Moonraker] Failed to merge filament metadata");
        }
    }

    /// <summary>
    /// Case-insensitive string extraction helper for filament fields
    /// </summary>
    private static string? GetFilamentString(JsonObject obj, string key)
    {
        try
        {
            // Try exact key first
            if (obj.TryGetPropertyValue(key, out var node) && node != null)
            {
                return node.ToString();
            }

            // Try uppercase variant
            var upperKey = key.ToUpperInvariant();
            if (obj.TryGetPropertyValue(upperKey, out var upperNode) && upperNode != null)
            {
                return upperNode.ToString();
            }

            // Try case-insensitive search
            foreach (var kvp in obj)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value?.ToString();
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Case-insensitive numeric extraction helper for filament fields. Returns null if not found or not numeric.
    /// </summary>
    private static double? GetFilamentDouble(JsonObject obj, string key)
    {
        try
        {
            // Try exact key first
            if (obj.TryGetPropertyValue(key, out var node) && node != null)
            {
                return TryGetDouble(node);
            }

            // Try uppercase variant
            var upperKey = key.ToUpperInvariant();
            if (obj.TryGetPropertyValue(upperKey, out var upperNode) && upperNode != null)
            {
                return TryGetDouble(upperNode);
            }

            // Try case-insensitive search
            foreach (var kvp in obj)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return TryGetDouble(kvp.Value);
                }
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
    public Uri? GetPrinterBaseUriFromStreamSource(string source)
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
    public async Task<JsonNode?> GetFileMetadataAsync(Uri baseUri, string filename, string? apiKey = null, string? authHeader = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(15) };
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
                var resp = await http.GetAsync(endpoint, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    var txt = await resp.Content.ReadAsStringAsync(cancellationToken);
                    return JsonNode.Parse(txt);
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
    public async Task<JsonNode?> GetPrintStatsAsync(Uri baseUri, string? apiKey = null, string? authHeader = null, CancellationToken cancellationToken = default)
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
            _logger.LogInformation("[Moonraker] GET {Endpoint}", endpoint);
            var resp = await http.GetAsync(endpoint, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                var txt = await resp.Content.ReadAsStringAsync(cancellationToken);
                return JsonNode.Parse(txt);
            }
            else
            {
                _logger.LogWarning("[Moonraker] print_stats request failed: {StatusCode} {ReasonPhrase}", (int)resp.StatusCode, resp.ReasonPhrase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Moonraker] GetPrintStatsAsync error");
        }
        return null;
    }

    /// <summary>
    /// Send a single G-code script/command to the printer via Moonraker.
    /// Posts JSON: { "script": "<command>" }
    /// Returns the parsed Moonraker JSON response or null on failure.
    /// </summary>
    public async Task<JsonNode?> SendGcodeScriptAsync(Uri baseUri, string command, string? apiKey = null, string? authHeader = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var header = string.IsNullOrWhiteSpace(authHeader) ? "X-Api-Key" : authHeader;
                try { http.DefaultRequestHeaders.Remove(header); } catch { }
                try { http.DefaultRequestHeaders.Add(header, apiKey); } catch { }
            }

            var json = System.Text.Json.JsonSerializer.Serialize(new { script = command });
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var endpoint = "/printer/gcode/script";
            _logger.LogInformation("[Moonraker] POST {Endpoint} -> {Command}", endpoint, command);
            var resp = await http.PostAsync(endpoint, content, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Moonraker] SendGcodeScriptAsync failed: {StatusCode} {ReasonPhrase}", (int)resp.StatusCode, resp.ReasonPhrase);
                return null;
            }
            var txt = await resp.Content.ReadAsStringAsync(cancellationToken);
            return JsonNode.Parse(txt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Moonraker] SendGcodeScriptAsync error");
            return null;
        }
    }

    // ... rest of MoonrakerClient methods remain unchanged (GetPrintInfoAsync, etc.)
}
