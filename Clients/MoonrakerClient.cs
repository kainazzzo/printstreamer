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
            // For now, this is a conservative stub: return null if we can't quickly fetch data.
            // Future: implement detailed parsing of /printer/objects/query or /printer/print_stats.
            using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(4) };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var header = string.IsNullOrWhiteSpace(authHeader) ? "X-Api-Key" : authHeader;
                try { http.DefaultRequestHeaders.Remove(header); } catch { }
                try { http.DefaultRequestHeaders.Add(header, apiKey); } catch { }
            }

            // Try a lightweight status endpoint commonly exposed by Moonraker frontends
            var candidates = new[] { "/printer/objects/query?print_stats", "/printer/print", "/printer/objects/query?display_status" };
            foreach (var ep in candidates)
            {
                try
                {
                    var resp = await http.GetAsync(ep, cancellationToken);
                    if (!resp.IsSuccessStatusCode) continue;
                    var txt = await resp.Content.ReadAsStringAsync(cancellationToken);
                    // Try to parse some common fields if present
                    try
                    {
                        var node = JsonNode.Parse(txt);
                        if (node == null) continue;
                        var info = new MoonrakerPrintInfo();
                        // Best-effort: search for strings/values in JSON
                        void Walk(JsonNode? n)
                        {
                            if (n == null) return;
                            if (n is JsonValue v)
                            {
                                var s = v.ToString();
                                if (string.IsNullOrWhiteSpace(info.Filename) && s.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase)) info.Filename = s;
                                if (string.IsNullOrWhiteSpace(info.State) && (s.Equals("printing", StringComparison.OrdinalIgnoreCase) || s.Equals("idle", StringComparison.OrdinalIgnoreCase))) info.State = s;
                            }
                            else if (n is JsonObject o)
                            {
                                foreach (var kv in o) Walk(kv.Value);
                            }
                            else if (n is JsonArray a)
                            {
                                foreach (var it in a) Walk(it);
                            }
                        }
                        Walk(node);
                        return info;
                    }
                    catch { }
                }
                catch { }
            }
        }
        catch { }
        return null;
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
            // Try candidates: as-is, and with common root prefix 'gcodes/' when not provided
            foreach (var candidate in EnumerateFilenameCandidates(filename))
            {
                var endpoint = $"/server/files/metadata?filename={Uri.EscapeDataString(candidate)}";
                Console.WriteLine($"[Moonraker] GET {endpoint}");
                var resp = await http.GetAsync(endpoint, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    var txt = await resp.Content.ReadAsStringAsync(cancellationToken);
                    return JsonNode.Parse(txt);
                }
                else
                {
                    Console.WriteLine($"[Moonraker] Metadata request failed for '{candidate}': {(int)resp.StatusCode} {resp.ReasonPhrase}");
                }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Download a file from Moonraker server files API: /server/files/download?filename=...
    /// Returns the file bytes or null on failure.
    /// </summary>
    public static async Task<byte[]?> DownloadFileAsync(Uri baseUri, string filename, string? apiKey = null, string? authHeader = null, CancellationToken cancellationToken = default)
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
            // Try candidates: as-is, and with common root prefix 'gcodes/' when not provided
            foreach (var candidate in EnumerateFilenameCandidates(filename))
            {
                var endpoint = $"/server/files/download?filename={Uri.EscapeDataString(candidate)}";
                Console.WriteLine($"[Moonraker] GET {endpoint}");
                var resp = await http.GetAsync(endpoint, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    return await resp.Content.ReadAsByteArrayAsync(cancellationToken);
                }
                else
                {
                    Console.WriteLine($"[Moonraker] Download failed for '{candidate}': {(int)resp.StatusCode} {resp.ReasonPhrase}");
                }
            }
            return null;
        }
        catch { return null; }
    }

    // Generate reasonable candidates for Moonraker file API: try the filename as-is, and if it lacks a
    // directory component, also try prefixing the common Mainsail root 'gcodes/'. Trim any leading slashes.
    private static IEnumerable<string> EnumerateFilenameCandidates(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) yield break;
        var f = filename.Trim();
        while (f.StartsWith("/")) f = f.Substring(1);
        yield return f;
        if (!f.Contains('/') && !f.StartsWith("gcodes/", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"gcodes/{f}";
        }
    }

    /// <summary>
    /// List files on the Moonraker server under an optional path (for example 'gcodes/').
    /// Returns the parsed JsonNode or null on failure. This is a best-effort helper and
    /// callers should handle different response shapes from various frontends.
    /// </summary>
    public static async Task<JsonNode?> ListFilesAsync(Uri baseUri, string? path = null, string? apiKey = null, string? authHeader = null, CancellationToken cancellationToken = default)
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

            var endpoint = "/server/files/list";
            if (!string.IsNullOrWhiteSpace(path)) endpoint += $"?path={Uri.EscapeDataString(path)}";
            Console.WriteLine($"[Moonraker] GET {endpoint}");
            var resp = await http.GetAsync(endpoint, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                var txt = await resp.Content.ReadAsStringAsync(cancellationToken);
                return JsonNode.Parse(txt);
            }
            else
            {
                Console.WriteLine($"[Moonraker] File list request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Moonraker] File list error: {ex.Message}");
        }
        return null;
    }

    // ... rest of MoonrakerClient methods remain unchanged (GetPrintInfoAsync, etc.)
}
