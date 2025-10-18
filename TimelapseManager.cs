using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

public class TimelapseManager : IDisposable
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
        var periodSec = config.GetValue<int?>("Timelapse:FramePeriodSeconds") ?? 60;
        _captureTimer = new Timer(CaptureFramesAsync, null, Timeout.Infinite, periodSec * 1000);
    }

    public string TimelapseDirectory => _mainTimelapseDir;

    public async Task<string?> StartTimelapseAsync(string sessionName, string? moonrakerFilename = null)
    {
        if (string.IsNullOrWhiteSpace(sessionName))
            return null;

        var sanitizedName = SanitizeFilename(moonrakerFilename ?? sessionName);
        if (_activeSessions.ContainsKey(sanitizedName))
        {
            Console.WriteLine($"[TimelapseManager] Session '{sanitizedName}' already active");
            return sanitizedName;
        }

        var session = new TimelapseSession
        {
            Name = sanitizedName,
            Service = new TimelapseService(_mainTimelapseDir, sanitizedName),
            StartTime = DateTime.UtcNow,
            LastCaptureTime = null
        };

        if (_activeSessions.TryAdd(sanitizedName, session))
        {
            Console.WriteLine($"[TimelapseManager] Started timelapse session: {sanitizedName}");
            
            // Capture initial frame
            await CaptureFrameForSessionAsync(session);
            
            // Start the timer if this is the first session
            if (_activeSessions.Count == 1)
            {
                var periodSec = _config.GetValue<int?>("Timelapse:FramePeriodSeconds") ?? 60;
                _captureTimer.Change(TimeSpan.FromSeconds(periodSec), TimeSpan.FromSeconds(periodSec));
                Console.WriteLine($"[TimelapseManager] Started capture timer (period: {periodSec}s)");
            }
            
            return sanitizedName;
        }

        session.Service?.Dispose();
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
            session.Service.Dispose();
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TimelapseManager] Failed to create video for {sessionName}: {ex.Message}");
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