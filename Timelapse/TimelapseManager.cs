using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using PrintStreamer.Utils;
using Microsoft.Extensions.Logging;
using PrintStreamer.Services;

namespace PrintStreamer.Timelapse
{
    public class TimelapseManager : IDisposable, PrintStreamer.Overlay.ITimelapseMetadataProvider
    {
    private readonly IConfiguration _config;
    private readonly ILogger<TimelapseManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MoonrakerClient _moonrakerClient;
    private readonly string _mainTimelapseDir;
    private readonly ConcurrentDictionary<string, TimelapseSession> _activeSessions = new();
    private readonly Timer _captureTimer;
    private bool _disposed = false;
    private readonly bool _verboseLogs;

    public TimelapseManager(IConfiguration config, ILoggerFactory loggerFactory, MoonrakerClient moonrakerClient)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TimelapseManager>();
        _moonrakerClient = moonrakerClient;
        _mainTimelapseDir = config.GetValue<string>("Timelapse:MainFolder") ?? Path.Combine(Directory.GetCurrentDirectory(), "timelapse");
        Directory.CreateDirectory(_mainTimelapseDir);
        _verboseLogs = _config.GetValue<bool?>("Timelapse:VerboseLogs") ?? false;

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
        var service = new TimelapseService(_mainTimelapseDir, sanitizedBase, _loggerFactory.CreateLogger<TimelapseService>());
        var actualFolderName = Path.GetFileName(service.OutputDir) ?? sanitizedBase;

        // Config: gate start until layer 1 by default
        var startAfterLayer1 = _config.GetValue<bool?>("Timelapse:StartAfterLayer1") ?? true;

        var session = new TimelapseSession
        {
            Name = actualFolderName,
            Service = service,
            StartTime = DateTime.UtcNow,
            LastCaptureTime = null,
            StartAfterLayer1 = startAfterLayer1,
            CaptureEnabled = !startAfterLayer1
        };

        // If a Moonraker filename was provided, try to download it once and cache contents + parsed layer markers
        if (!string.IsNullOrWhiteSpace(moonrakerFilename))
        {
            try
            {
                var baseUrl = _config.GetValue<string>("Moonraker:BaseUrl") ?? "http://localhost:7125";
                var apiKey = _config.GetValue<string>("Moonraker:ApiKey");
                var authHeader = _config.GetValue<string>("Moonraker:AuthHeader");
                var baseUri = _moonrakerClient.GetPrinterBaseUriFromStreamSource(baseUrl) ?? new Uri(baseUrl);

                Console.WriteLine($"[TimelapseManager] Skipping remote G-code download/list for: {moonrakerFilename}.\n[TimelapseManager] Timelapse now expects slicer to embed print stats via SET_PRINT_STATS_INFO.");
                // Instead of downloading or listing gcode, rely on server-side print_stats provided by the printer
                try
                {
                    var meta = await _moonrakerClient.GetFileMetadataAsync(baseUri, moonrakerFilename!, apiKey, authHeader, CancellationToken.None);
                    if (meta != null)
                    {
                        session.MetadataRaw = meta;
                        // Try to extract some common metadata fields without assuming layer markers in the file
                        try
                        {
                            if (meta is JsonObject m && m.TryGetPropertyValue("result", out var res) && res is JsonObject r)
                            {
                                if (r.TryGetPropertyValue("metadata", out var md) && md is JsonObject mdObj)
                                {
                                    if (mdObj.TryGetPropertyValue("slicer", out var slicer)) session.Slicer = slicer?.ToString();
                                    if (mdObj.TryGetPropertyValue("estimated_time", out var est)) session.EstimatedSeconds = TryParseDouble(est?.ToString());
                                    if (mdObj.TryGetPropertyValue("layer_count", out var lc) && int.TryParse(lc?.ToString(), out var lcInt)) session.TotalLayersFromMetadata = lcInt;
                                    // Filament totals (support variants and units)
                                    session.FilamentTotalMm = TryParseDouble(mdObj["filament_total_mm"]?.ToString())
                                                            ?? TryParseDouble(mdObj["filament_total"]?.ToString())
                                                            ?? (TryParseDouble(mdObj["filament_total_m"]?.ToString()) * 1000.0);
                                }
                                if (session.TotalLayersFromMetadata == null && r.TryGetPropertyValue("layer_count", out var topLc) && int.TryParse(topLc?.ToString(), out var topLcInt))
                                    session.TotalLayersFromMetadata = topLcInt;
                                // Some Moonraker setups place filament_total_mm at top level
                                if (session.FilamentTotalMm == null && r.TryGetPropertyValue("filament_total_mm", out var ftm) && ftm != null)
                                {
                                    session.FilamentTotalMm = TryParseDouble(ftm.ToString());
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TimelapseManager] Error downloading/parsing G-code: {ex.Message}");
            }
        }

        if (_activeSessions.TryAdd(actualFolderName, session))
        {
            Console.WriteLine($"[TimelapseManager] Started timelapse session: {actualFolderName}");
            Console.WriteLine($"[TimelapseManager]   Output directory: {session.Service.OutputDir}");
            
            if (session.StartAfterLayer1)
            {
                Console.WriteLine($"[TimelapseManager]   Gating enabled: frames will start when layer >= 1");
            }

            // Capture initial frame only if not gating start until layer 1
            if (!session.StartAfterLayer1)
            {
                await CaptureFrameForSessionAsync(session);
            }

            // Start the timer if this is the first session
            if (_activeSessions.Count == 1)
            {
                var timelapsePeriod = _config.GetValue<TimeSpan?>("Timelapse:Period") ?? TimeSpan.FromMinutes(1);
                _captureTimer.Change(timelapsePeriod, timelapsePeriod);
                Console.WriteLine($"[TimelapseManager] Started capture timer with period: {timelapsePeriod}");
                Console.WriteLine($"[TimelapseManager]   Timer will fire every {timelapsePeriod.TotalSeconds} seconds");
                Console.WriteLine($"[TimelapseManager]   Stream source: {_config.GetValue<string>("Stream:Source")}");
            }
            else
            {
                Console.WriteLine($"[TimelapseManager] Timer already running ({_activeSessions.Count} active sessions)");
            }

            return actualFolderName;
        }

        // If we couldn't add the session for some reason, clean up
        service?.Dispose();
        return null;
    }

    /// <summary>
    /// Notify the manager of current print layer progress for a named session.
    /// When currentLayer >= 1 capture is enabled. When currentLayer >= (totalLayers - offset) capture stops.
    /// </summary>
    public void NotifyPrintProgress(string? sessionName, int? currentLayer, int? totalLayers)
    {
        if (string.IsNullOrWhiteSpace(sessionName)) return;
        if (!_activeSessions.TryGetValue(sessionName, out var session)) return;

        try
        {
            // Enable capture once we reach layer 1 (skip leveling at layer 0)
            if (session.StartAfterLayer1 && !session.CaptureEnabled)
            {
                if (currentLayer.HasValue && currentLayer.Value >= 1)
                {
                    session.CaptureEnabled = true;
                    session.LoggedWaitingForLayer = false;
                    Console.WriteLine($"[TimelapseManager] Starting frame capture at layer {currentLayer} for session {sessionName}");
                }
                else if (!session.LoggedWaitingForLayer)
                {
                    // Log once that we're waiting for layer info (always log, not just verbose)
                    Console.WriteLine($"[TimelapseManager] Waiting for layer >= 1 before capturing frames for {sessionName} (current: {currentLayer?.ToString() ?? "n/a"})");
                    session.LoggedWaitingForLayer = true;
                }
            }

            // If we have the total layers and current layer is at or past the configured threshold, stop the session
            var layerOffset = _config.GetValue<int?>("Timelapse:LastLayerOffset") ?? 1;
            if (layerOffset < 0) layerOffset = 0;
            if (currentLayer.HasValue && totalLayers.HasValue && totalLayers.Value > 0)
            {
                var triggerLayer = Math.Max(0, totalLayers.Value - layerOffset);
                if (currentLayer.Value >= triggerLayer)
                {
                    Console.WriteLine($"[TimelapseManager] Print reached last-layer threshold for session {sessionName} ({currentLayer}/{totalLayers}, offset={layerOffset}) - stopping further captures.");
                    // Mark session as stopped to prevent timer from capturing more frames
                    session.IsStopped = true;
                    if (_verboseLogs)
                    {
                        Console.WriteLine($"[TimelapseManager] Session {sessionName} marked as stopped (will be finalized by caller)");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TimelapseManager] Error in NotifyPrintProgress for {sessionName}: {ex.Message}");
        }
    }

    public async Task<string?> StopTimelapseAsync(string sessionName)
    {
        if (!_activeSessions.TryGetValue(sessionName, out var session))
        {
            Console.WriteLine($"[TimelapseManager] Cannot stop timelapse: session '{sessionName}' not found");
            return null;
        }

        Console.WriteLine($"[TimelapseManager] Stopping timelapse session: {sessionName}");
        Console.WriteLine($"[TimelapseManager]   Frames captured: {session.Service.OutputDir}");

        // Mark session as stopped first to prevent any more captures
        session.IsStopped = true;
        
        // Give any in-flight timer callback a moment to check the flag
        await Task.Delay(100);
        
        // Now remove the session
        _activeSessions.TryRemove(sessionName, out _);

        // Stop timer if no more sessions
        if (_activeSessions.IsEmpty)
        {
            _captureTimer.Change(Timeout.Infinite, Timeout.Infinite);
            Console.WriteLine($"[TimelapseManager] Stopped capture timer (no active sessions remaining)");
        }
        else
        {
            Console.WriteLine($"[TimelapseManager] Timer still running ({_activeSessions.Count} active sessions remaining)");
        }

    // Create video
        var folderName = Path.GetFileName(session.Service.OutputDir);
        var videoPath = Path.Combine(session.Service.OutputDir, $"{folderName}.mp4");
        
        Console.WriteLine($"[TimelapseManager] Creating video: {videoPath}");
        
        try
        {
            var result = await session.Service.CreateVideoAsync(videoPath, 30, CancellationToken.None);
            // Clear cached metadata
            session.LayerStarts = null;
            session.MetadataRaw = null;
            session.Service.Dispose();
            
            if (result != null)
            {
                Console.WriteLine($"[TimelapseManager] Successfully created timelapse video: {result}");
            }
            else
            {
                Console.WriteLine($"[TimelapseManager] Failed to create timelapse video (result was null)");
            }
            
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TimelapseManager] Failed to create video for {sessionName}: {ex.Message}");
            Console.WriteLine($"[TimelapseManager] Stack trace: {ex.StackTrace}");
            // Ensure cleanup
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
                TotalLayersFromMetadata = session.TotalLayersFromMetadata,
                RawMetadata = session.MetadataRaw,
                Slicer = session.Slicer,
                    EstimatedSeconds = session.EstimatedSeconds,
                    FilamentTotalMm = session.FilamentTotalMm
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
                    TotalLayersFromMetadata = s.TotalLayersFromMetadata,
                    RawMetadata = s.MetadataRaw,
                    Slicer = s.Slicer,
                    EstimatedSeconds = s.EstimatedSeconds,
                    FilamentTotalMm = s.FilamentTotalMm
                };
            }
        }

        return null;
    }

    private void CaptureFramesAsync(object? state)
    {
        // Fire and forget pattern - don't use async void which can cause timer to stop
        _ = Task.Run(async () =>
        {
            try
            {
                if (_disposed || _activeSessions.IsEmpty)
                    return;

                var streamSource = _config.GetValue<string>("Stream:Source");
                if (string.IsNullOrWhiteSpace(streamSource))
                {
                    Console.WriteLine("[TimelapseManager] No stream source configured, skipping frame capture");
                    return;
                }

                // Diagnostic: show how many sessions are being scanned on each tick
                if (_verboseLogs)
                {
                    Console.WriteLine($"[TimelapseManager] Timer tick: {_activeSessions.Count} active session(s)");
                }

                foreach (var session in _activeSessions.Values)
                {
                    // Skip sessions that have been marked as stopped
                    if (session.IsStopped)
                    {
                        if (_verboseLogs)
                        {
                            Console.WriteLine($"[TimelapseManager] Skipping frame capture for stopped session: {session.Name}");
                        }
                        continue;
                    }

                    // Defer capture until we reach layer 1 if configured
                    if (session.StartAfterLayer1 && !session.CaptureEnabled)
                    {
                        if (_verboseLogs)
                        {
                            Console.WriteLine($"[TimelapseManager] Waiting for layer 1 before capturing frames for session: {session.Name}");
                        }
                        continue;
                    }
                    
                    await CaptureFrameForSessionAsync(session);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TimelapseManager] Error in timer callback: {ex.Message}");
                Console.WriteLine($"[TimelapseManager] Stack trace: {ex.StackTrace}");
            }
        });
    }

    private async Task CaptureFrameForSessionAsync(TimelapseSession session)
    {
        try
        {
            // Guard: if session was marked stopped, don't attempt capture
            if (session.IsStopped)
            {
                if (_verboseLogs)
                {
                    Console.WriteLine($"[TimelapseManager] Session {session.Name} is stopped; skipping capture.");
                }
                return;
            }

            // Guard: if gating until layer 1 and not yet enabled, skip
            if (session.StartAfterLayer1 && !session.CaptureEnabled)
            {
                if (_verboseLogs)
                {
                    Console.WriteLine($"[TimelapseManager] Capture gated until layer 1 for session {session.Name}; skipping capture.");
                }
                return;
            }

            var streamSource = _config.GetValue<string>("Stream:Source");
            if (string.IsNullOrWhiteSpace(streamSource))
            {
                Console.WriteLine($"[TimelapseManager] No stream source configured; cannot capture frame for {session.Name}");
                return;
            }

            if (_verboseLogs)
            {
                Console.WriteLine($"[TimelapseManager] Fetching frame from: {streamSource}");
            }

            var frame = await FetchSingleJpegFrameAsync(streamSource, 10, CancellationToken.None);
            if (frame != null)
            {
                // Guard again right before saving in case stop flag flipped during fetch
                if (session.IsStopped)
                {
                    if (_verboseLogs)
                    {
                        Console.WriteLine($"[TimelapseManager] Session {session.Name} was stopped after fetch; dropping frame.");
                    }
                    return;
                }
                await session.Service.SaveFrameAsync(frame, CancellationToken.None);
                session.LastCaptureTime = DateTime.UtcNow;
                Console.WriteLine($"[TimelapseManager] Captured frame for {session.Name} ({frame.Length} bytes)");
            }
            else
            {
                Console.WriteLine($"[TimelapseManager] Failed to capture frame for {session.Name} - frame was null");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TimelapseManager] Error capturing frame for {session.Name}: {ex.Message}");
            if (_verboseLogs)
            {
                Console.WriteLine($"[TimelapseManager] Stack trace: {ex.StackTrace}");
            }
        }
    }

    // Utility: Extract a single JPEG frame from MJPEG stream URL (copied from Program.cs)
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _captureTimer.Dispose();
        foreach (var kv in _activeSessions)
        {
            try { kv.Value.Service.Dispose(); } catch { }
        }
        _activeSessions.Clear();
    }
    }

    public class TimelapseSession
    {
    public string Name { get; set; } = string.Empty;
    public TimelapseService Service { get; set; } = default!;
    public DateTime StartTime { get; set; }
    public DateTime? LastCaptureTime { get; set; }
    public long[]? LayerStarts { get; set; }
    public int? TotalLayersFromMetadata { get; set; }
    public JsonNode? MetadataRaw { get; set; }
    public string? Slicer { get; set; }
    public double? EstimatedSeconds { get; set; }
    public double? FilamentTotalMm { get; set; }
    public bool IsStopped { get; set; } = false;
    // Gating: start capturing only when first printing layer begins
    public bool StartAfterLayer1 { get; set; } = true;
    public bool CaptureEnabled { get; set; } = false;
    public bool LoggedWaitingForLayer { get; set; } = false;
    }

    public class TimelapseInfo
    {
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int FrameCount { get; set; }
    public string[] VideoFiles { get; set; } = Array.Empty<string>();
    public DateTime StartTime { get; set; }
    public DateTime? LastFrameTime { get; set; }
    }
}
