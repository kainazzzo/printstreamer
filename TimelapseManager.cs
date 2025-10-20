using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

public class TimelapseManager : IDisposable, PrintStreamer.Overlay.ITimelapseMetadataProvider
{
    private readonly IConfiguration _config;
    private readonly string _mainTimelapseDir;
    private readonly ConcurrentDictionary<string, TimelapseSession> _activeSessions = new();
    private readonly Timer _captureTimer;
    private bool _disposed = false;

    public TimelapseManager(IConfiguration config)
    {
        _config = config;
        _mainTimelapseDir = config.GetValue<string>("Timelapse:MainFolder") ?? Path.Combine(Directory.GetCurrentDirectory(), "timelapse");
        Directory.CreateDirectory(_mainTimelapseDir);

        // Set up periodic capture timer (disabled by default, enabled when sessions are active)
        var timelapsePeriod = config.GetValue<TimeSpan?>("Timelapse:Period") ?? TimeSpan.FromMinutes(1);
        _captureTimer = new Timer(CaptureFramesAsync, null, Timeout.Infinite, (int)timelapsePeriod.TotalMilliseconds);
    }

    public string TimelapseDirectory => _mainTimelapseDir;

    public async Task<string?> StartTimelapseAsync(string sessionName, string? moonrakerFilename = null)
    {
        if (string.IsNullOrWhiteSpace(sessionName))
            return null;

        var sanitizedBase = SanitizeFilename(moonrakerFilename ?? sessionName);

        // Create the service first so it can create an output folder and possibly append a suffix
        var service = new TimelapseService(_mainTimelapseDir, sanitizedBase);
        var actualFolderName = Path.GetFileName(service.OutputDir) ?? sanitizedBase;

        var session = new TimelapseSession
        {
            Name = actualFolderName,
            Service = service,
            StartTime = DateTime.UtcNow,
            LastCaptureTime = null
        };

        // If a Moonraker filename was provided, try to download it once and cache contents + parsed layer markers
        if (!string.IsNullOrWhiteSpace(moonrakerFilename))
        {
            try
            {
                var baseUrl = _config.GetValue<string>("Moonraker:BaseUrl") ?? "http://localhost:7125";
                var apiKey = _config.GetValue<string>("Moonraker:ApiKey");
                var authHeader = _config.GetValue<string>("Moonraker:AuthHeader");
                var baseUri = MoonrakerClient.GetPrinterBaseUriFromStreamSource(baseUrl) ?? new Uri(baseUrl);

                Console.WriteLine($"[TimelapseManager] Attempting to download G-code: {moonrakerFilename}");
                // Best-effort: list remote gcode files under common roots to help debug 404s
                try
                {
                    var listRootCandidates = new[] { "", "gcodes", "/gcodes" };
                    foreach (var root in listRootCandidates)
                    {
                        var list = await MoonrakerClient.ListFilesAsync(baseUri, string.IsNullOrWhiteSpace(root) ? null : root, apiKey, authHeader, CancellationToken.None);
                        if (list != null)
                        {
                            try
                            {
                                // The response shape varies; try to extract file names from common locations
                                Console.WriteLine($"[TimelapseManager] Moonraker file list for '{root}':");
                                // If result.files exists as an array
                                var files = list["result"]? ["files"];
                                if (files is JsonArray fa)
                                {
                                    foreach (var f in fa)
                                    {
                                        var name = f?["name"]?.ToString() ?? f?.ToString();
                                        if (!string.IsNullOrWhiteSpace(name) && name.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
                                            Console.WriteLine($"[TimelapseManager] - {name}");
                                    }
                                }
                                else
                                {
                                    // Walk the JSON and print any gcode-looking strings
                                    void Walk(JsonNode? n)
                                    {
                                        if (n == null) return;
                                        if (n is JsonValue v)
                                        {
                                            var s = v.ToString();
                                            if (!string.IsNullOrWhiteSpace(s) && s.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase))
                                                Console.WriteLine($"[TimelapseManager] - {s}");
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
                                    Walk(list);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                var bytes = await MoonrakerClient.DownloadFileAsync(baseUri, moonrakerFilename, apiKey, authHeader, CancellationToken.None);
                if (bytes != null && bytes.Length > 0)
                {
                    session.CachedGcode = bytes;
                    // Save a copy to disk under configured folder for inspection
                    try
                    {
                        var gcodeFolder = _config.GetValue<string>("GcodeFolder") ?? Path.Combine(_mainTimelapseDir, "gcode");
                        Directory.CreateDirectory(gcodeFolder);
                        var safeName = SanitizeFilename(moonrakerFilename ?? session.Name);
                        var savePath = Path.Combine(gcodeFolder, safeName + ".gcode");
                        await File.WriteAllBytesAsync(savePath, bytes);
                        session.SavedGcodePath = savePath;
                        Console.WriteLine($"[TimelapseManager] Saved G-code to: {savePath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TimelapseManager] Failed to save G-code to disk: {ex.Message}");
                    }
                    // Parse layer markers from G-code (Mainsail-style: look for ;LAYER: or ; layer or ;LAYER: markers)
                    session.LayerStarts = ParseGcodeLayerStarts(bytes);
                    session.TotalLayersFromGcode = session.LayerStarts?.Length;
                    Console.WriteLine($"[TimelapseManager] Downloaded and parsed G-code ({session.TotalLayersFromGcode ?? 0} layers)");

                    // Try to also fetch server file metadata (slicer, estimated times etc.)
                    var meta = await MoonrakerClient.GetFileMetadataAsync(baseUri, moonrakerFilename!, apiKey, authHeader, CancellationToken.None);
                    if (meta != null)
                    {
                        session.MetadataRaw = meta;
                        // Try to extract some common metadata fields
                        try
                        {
                            if (meta is JsonObject m && m.TryGetPropertyValue("result", out var res) && res is JsonObject r)
                            {
                                if (r.TryGetPropertyValue("metadata", out var md) && md is JsonObject mdObj)
                                {
                                    if (mdObj.TryGetPropertyValue("slicer", out var slicer)) session.Slicer = slicer?.ToString();
                                    if (mdObj.TryGetPropertyValue("estimated_time", out var est)) session.EstimatedSeconds = TryParseDouble(est?.ToString());
                                    if (mdObj.TryGetPropertyValue("layer_count", out var lc) && int.TryParse(lc?.ToString(), out var lcInt)) session.TotalLayersFromMetadata = lcInt;
                                }
                                // some frontends place layer_count at result
                                if (session.TotalLayersFromMetadata == null && r.TryGetPropertyValue("layer_count", out var topLc) && int.TryParse(topLc?.ToString(), out var topLcInt))
                                    session.TotalLayersFromMetadata = topLcInt;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TimelapseManager] Error downloading/parsing G-code: {ex.Message}");
            }
        }

        if (_activeSessions.TryAdd(actualFolderName, session))
        {
            Console.WriteLine($"[TimelapseManager] Started timelapse session: {actualFolderName}");

            // Capture initial frame
            await CaptureFrameForSessionAsync(session);

            // Start the timer if this is the first session
            if (_activeSessions.Count == 1)
            {
                var timelapsePeriod = _config.GetValue<TimeSpan?>("Timelapse:Period") ?? TimeSpan.FromMinutes(1);
                _captureTimer.Change(timelapsePeriod, timelapsePeriod);
                Console.WriteLine($"[TimelapseManager] Started capture timer (period: {timelapsePeriod})");
            }

            return actualFolderName;
        }

        // If we couldn't add the session for some reason, clean up
        service?.Dispose();
        return null;
    }

    public async Task<string?> StopTimelapseAsync(string sessionName)
    {
        if (!_activeSessions.TryRemove(sessionName, out var session))
            return null;

        Console.WriteLine($"[TimelapseManager] Stopping timelapse session: {sessionName}");

        // Stop timer if no more sessions
        if (_activeSessions.IsEmpty)
        {
            _captureTimer.Change(Timeout.Infinite, Timeout.Infinite);
            Console.WriteLine($"[TimelapseManager] Stopped capture timer");
        }

    // Create video
        var folderName = Path.GetFileName(session.Service.OutputDir);
        var videoPath = Path.Combine(session.Service.OutputDir, $"{folderName}.mp4");
        
        try
        {
            var result = await session.Service.CreateVideoAsync(videoPath, 30, CancellationToken.None);
            // Clear cached gcode and metadata
            session.CachedGcode = null;
            session.LayerStarts = null;
            session.MetadataRaw = null;
            session.Service.Dispose();
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TimelapseManager] Failed to create video for {sessionName}: {ex.Message}");
            // Ensure cleanup
            session.CachedGcode = null;
            session.LayerStarts = null;
            session.MetadataRaw = null;
            session.Service.Dispose();
            return null;
        }
    }

    public async Task StopAllTimelapsesAsync()
    {
        var sessions = _activeSessions.Keys.ToArray();
        foreach (var sessionName in sessions)
        {
            await StopTimelapseAsync(sessionName);
        }
    }

    public IEnumerable<TimelapseInfo> GetAllTimelapses()
    {
        var timelapses = new List<TimelapseInfo>();

        if (!Directory.Exists(_mainTimelapseDir))
            return timelapses;

        foreach (var dir in Directory.GetDirectories(_mainTimelapseDir))
        {
            var dirName = Path.GetFileName(dir);
            var isActive = _activeSessions.ContainsKey(dirName);
            
            var frameFiles = Directory.GetFiles(dir, "frame_*.jpg").OrderBy(f => f).ToArray();
            var videoFiles = Directory.GetFiles(dir, "*.mp4").ToArray();
            
            var info = new TimelapseInfo
            {
                Name = dirName,
                Path = dir,
                IsActive = isActive,
                FrameCount = frameFiles.Length,
                VideoFiles = videoFiles.Select(f => Path.GetFileName(f) ?? "unknown").ToArray(),
                StartTime = isActive && _activeSessions.TryGetValue(dirName, out var session) 
                    ? session.StartTime 
                    : Directory.GetCreationTime(dir),
                LastFrameTime = frameFiles.Length > 0 
                    ? File.GetLastWriteTime(frameFiles.Last()) 
                    : (DateTime?)null
            };
            
            timelapses.Add(info);
        }

        return timelapses.OrderByDescending(t => t.StartTime);
    }

    public IEnumerable<string> GetActiveSessionNames() => _activeSessions.Keys;

    // Provide a thread-safe accessor for overlay/other services to read cached metadata for a given printer filename
    public PrintStreamer.Overlay.TimelapseSessionMetadata? GetMetadataForFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;

        // Attempt to match by sanitized filename to session folder names
        var sanitized = SanitizeFilename(filename);
        if (_activeSessions.TryGetValue(sanitized, out var session))
        {
            return new PrintStreamer.Overlay.TimelapseSessionMetadata
            {
                TotalLayersFromGcode = session.TotalLayersFromGcode,
                TotalLayersFromMetadata = session.TotalLayersFromMetadata,
                RawMetadata = session.MetadataRaw,
                Slicer = session.Slicer,
                    EstimatedSeconds = session.EstimatedSeconds,
                    SavedGcodePath = session.SavedGcodePath
            };
        }

        // If direct key didn't match, try to find any session whose name equals or contains the sanitized filename
        foreach (var kv in _activeSessions)
        {
            if (kv.Key.Equals(sanitized, StringComparison.OrdinalIgnoreCase) || kv.Key.Contains(sanitized, StringComparison.OrdinalIgnoreCase))
            {
                var s = kv.Value;
                return new PrintStreamer.Overlay.TimelapseSessionMetadata
                {
                    TotalLayersFromGcode = s.TotalLayersFromGcode,
                    TotalLayersFromMetadata = s.TotalLayersFromMetadata,
                    RawMetadata = s.MetadataRaw,
                    Slicer = s.Slicer,
                    EstimatedSeconds = s.EstimatedSeconds,
                    SavedGcodePath = s.SavedGcodePath
                };
            }
        }

        return null;
    }

    private async void CaptureFramesAsync(object? state)
    {
        if (_disposed || _activeSessions.IsEmpty)
            return;

        var streamSource = _config.GetValue<string>("Stream:Source");
        if (string.IsNullOrWhiteSpace(streamSource))
            return;

        foreach (var session in _activeSessions.Values)
        {
            await CaptureFrameForSessionAsync(session);
        }
    }

    private async Task CaptureFrameForSessionAsync(TimelapseSession session)
    {
        try
        {
            var streamSource = _config.GetValue<string>("Stream:Source");
            if (string.IsNullOrWhiteSpace(streamSource))
                return;

            var frame = await FetchSingleJpegFrameAsync(streamSource, 10, CancellationToken.None);
            if (frame != null)
            {
                await session.Service.SaveFrameAsync(frame, CancellationToken.None);
                session.LastCaptureTime = DateTime.UtcNow;
                Console.WriteLine($"[TimelapseManager] Captured frame for {session.Name}");
            }
            else
            {
                Console.WriteLine($"[TimelapseManager] Failed to capture frame for {session.Name}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TimelapseManager] Error capturing frame for {session.Name}: {ex.Message}");
        }
    }

    // Parse gcode bytes and return an array of file offsets where layer markers were found
    private static long[] ParseGcodeLayerStarts(byte[] bytes)
    {
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            // Mainsail / many slicers annotate layers with lines like: ";LAYER:10" or "; layer 10" or ";LAYER: 10"
            var matches = Regex.Matches(text, @"^\s*;\s*(?:LAYER:|layer\s+)(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var offsets = new List<long>();
            foreach (Match m in matches)
            {
                if (m.Success && int.TryParse(m.Groups[1].Value, out var layerIdx))
                {
                    // compute byte offset by finding the match index in the raw text
                    var charIndex = m.Index;
                    var byteOffset = Encoding.UTF8.GetByteCount(text.Substring(0, charIndex));
                    offsets.Add(byteOffset);
                }
            }
            return offsets.ToArray();
        }
        catch { return Array.Empty<long>(); }
    }

    private static double? TryParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s, out var d)) return d;
        return null;
    }

    private static string SanitizeFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return "unknown";
        
        // Remove file extension
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
        
        // Define characters to remove or replace
        var invalidChars = Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Concat(new[] { ' ', '-', '(', ')', '[', ']', '{', '}', ':', ';', ',', '.', '#' })
            .Distinct()
            .ToArray();
        
        var result = nameWithoutExtension;
        
        // Replace invalid characters
        foreach (var c in invalidChars)
        {
            result = result.Replace(c, '_');
        }
        
        // Special replacements
        result = result.Replace("&", "and");
        
        // Clean up multiple underscores
        while (result.Contains("__"))
        {
            result = result.Replace("__", "_");
        }
        
        // Trim underscores from start and end
        result = result.Trim('_');
        
        // Ensure we have a valid result
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    // Move this from Program.cs to be accessible here
    private static async Task<byte[]?> FetchSingleJpegFrameAsync(string mjpegUrl, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        using var resp = await client.GetAsync(mjpegUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        int bytesRead;
        // Read until we find a JPEG frame
        while (!cancellationToken.IsCancellationRequested)
        {
            bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            if (bytesRead == 0) break;
            ms.Write(buffer, 0, bytesRead);
            if (MjpegReader.TryExtractJpeg(ms, out var jpegBytes) && jpegBytes != null)
            {
                return jpegBytes;
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _captureTimer?.Dispose();
            
            // Stop all active sessions
            foreach (var session in _activeSessions.Values)
            {
                session.Service?.Dispose();
            }
            _activeSessions.Clear();
            
            _disposed = true;
        }
    }
}

public class TimelapseSession
{
    public required string Name { get; set; }
    public required TimelapseService Service { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? LastCaptureTime { get; set; }
    // Cached G-code bytes (downloaded once at session start when available)
    public byte[]? CachedGcode { get; set; }
    // Parsed byte offsets where each layer starts (zero-based layer index -> file offset)
    public long[]? LayerStarts { get; set; }
    // Total layers inferred from parsed G-code
    public int? TotalLayersFromGcode { get; set; }
    // Total layers as reported in file metadata (if available)
    public int? TotalLayersFromMetadata { get; set; }
    // Raw metadata JSON from Moonraker /server/files/metadata
    public JsonNode? MetadataRaw { get; set; }
    // Slicer name and estimated seconds
    public string? Slicer { get; set; }
    public double? EstimatedSeconds { get; set; }
    // Path on disk where the downloaded G-code was saved (if saved)
    public string? SavedGcodePath { get; set; }
}

public class TimelapseInfo
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public bool IsActive { get; set; }
    public int FrameCount { get; set; }
    public string[] VideoFiles { get; set; } = Array.Empty<string>();
    public DateTime StartTime { get; set; }
    public DateTime? LastFrameTime { get; set; }
}