using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

public class TimelapseService : IDisposable
{
    public string OutputDir { get; }
    private readonly ILogger<TimelapseService> _logger;
    private int _frameCount = 0;
    private bool _finalized = false;
    private readonly object _saveLock = new object();
    private bool _isAcceptingFrames = true; // true = accepting, false = stopped

    public TimelapseService(string mainFolder, string streamId, ILogger<TimelapseService> logger)
    {
        _logger = logger;
        // mainFolder: base timelapse directory (configurable)
        // streamId: unique per stream/job (timestamp, job name, etc)

        // Ensure the main folder exists
        Directory.CreateDirectory(mainFolder);

        var baseName = streamId;
        var candidate = Path.Combine(mainFolder, baseName);

        if (!Directory.Exists(candidate))
        {
            OutputDir = candidate;
            Directory.CreateDirectory(OutputDir);
            return;
        }

        // Directory exists already. Find the highest numeric suffix for folders matching
        // baseName, baseName_1, baseName_2, ... and pick next index.
        // Match pattern: ^baseName(?:_(\d+))?$
        var rx = new Regex($"^{Regex.Escape(baseName)}(?:_(\\d+))?$", RegexOptions.Compiled);
        var max = -1;

        foreach (var dir in Directory.GetDirectories(mainFolder))
        {
            var name = Path.GetFileName(dir);
            if (name == null) continue;
            var m = rx.Match(name);
            if (!m.Success) continue;
            if (m.Groups.Count > 1 && m.Groups[1].Success)
            {
                if (int.TryParse(m.Groups[1].Value, out var v))
                {
                    if (v > max) max = v;
                }
            }
            else
            {
                // The base folder without suffix exists; treat it as index 0
                if (0 > max) max = 0;
            }
        }

        var next = (max >= 0) ? max + 1 : 1;
        var newName = $"{baseName}_{next}";
        OutputDir = Path.Combine(mainFolder, newName);
        Directory.CreateDirectory(OutputDir);
    }

    /// <summary>
    /// Create a TimelapseService that purposely reuses an existing folder name (no numeric suffixing).
    /// This is useful for resuming timelapse capture into an existing folder on restart.
    /// </summary>
    public TimelapseService(string mainFolder, string streamId, ILogger<TimelapseService> logger, bool reuseExisting)
    {
        _logger = logger;
        Directory.CreateDirectory(mainFolder);
        var candidate = Path.Combine(mainFolder, streamId);
        OutputDir = candidate;
        Directory.CreateDirectory(OutputDir);
    }

    public async Task SaveFrameAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        if (imageBytes == null) return;
        int myIndex;
        lock (_saveLock)
        {
            if (!_isAcceptingFrames)
                return; // no longer accepting frames
            myIndex = _frameCount;
            Interlocked.Increment(ref _frameCount);
        }

        var filename = Path.Combine(OutputDir, $"frame_{myIndex:D6}.jpg");
        await File.WriteAllBytesAsync(filename, imageBytes, cancellationToken);
    }

    public async Task<string?> CreateVideoAsync(string outputVideoPath, int fps = 30, CancellationToken cancellationToken = default)
    {
        if (_finalized)
        {
            _logger.LogInformation("Video already created or in progress.");
            return null;
        }
        _finalized = true;
        
        _logger.LogInformation("Captured {FrameCount} frames.", _frameCount);
        
        if (_frameCount == 0)
        {
            _logger.LogInformation("No frames captured, skipping video creation.");
            return null;
        }
        
        // List frame files for debugging
        var frameFiles = Directory.GetFiles(OutputDir, "frame_*.jpg").OrderBy(f => f).ToArray();
        _logger.LogInformation("Found {Count} frame files on disk.", frameFiles.Length);
        if (frameFiles.Length <= 10)
        {
            foreach (var file in frameFiles)
            {
                var fileName = Path.GetFileName(file);
                var fileSize = new FileInfo(file).Length;
                _logger.LogInformation("  {FileName} ({FileSize} bytes)", fileName, fileSize);
            }
        }
        else
        {
            _logger.LogInformation("  {First} to {Last} ({Count} files)", Path.GetFileName(frameFiles[0]), Path.GetFileName(frameFiles[^1]), frameFiles.Length);
        }
        
        if (frameFiles.Length == 0)
        {
            _logger.LogInformation("No frame files found on disk, skipping video creation.");
            return null;
        }
        
        // Use ffmpeg to create video from images
        // Enhanced command with better compatibility and error handling
        _logger.LogInformation("Creating video at {Fps} fps: {OutputPath}", fps, outputVideoPath);
        
        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(outputVideoPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }
        
        // Use more robust ffmpeg arguments:
        // -y: overwrite output file
        // -framerate: input framerate
        // -start_number 0: start from frame_000000.jpg
        // -i: input pattern
    // -c:v libx264: use H.264 codec
    // -preset medium: balance between speed and compression
    // -crf 18: higher quality for uploads (lower = better quality). You can
    // adjust this depending on CPU/time vs final quality needs.
        // -pix_fmt yuv420p: ensure compatibility with most players
        // -movflags +faststart: optimize for web playback
    var arguments = $"-y -framerate {fps} -start_number 0 -i \"{OutputDir}/frame_%06d.jpg\" -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -movflags +faststart \"{outputVideoPath}\"";
        
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        
        _logger.LogInformation("Running: ffmpeg {Arguments}", arguments);
        
        using var proc = Process.Start(psi);
        if (proc == null)
        {
            _logger.LogError("Failed to start ffmpeg for timelapse.");
            return null;
        }
        
        // Read both stdout and stderr for complete debugging info
        var errorOutput = await proc.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutput = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
        
        await proc.WaitForExitAsync(cancellationToken);
        if (proc.ExitCode == 0)
        {
            _logger.LogInformation("Video created successfully: {OutputPath}", outputVideoPath);
            
            // Verify the file was actually created and has content
            if (File.Exists(outputVideoPath))
            {
                var fileInfo = new FileInfo(outputVideoPath);
                _logger.LogInformation("Video file size: {Size} bytes", fileInfo.Length);
                if (fileInfo.Length == 0)
                {
                    _logger.LogWarning("Output file is empty!");
                    return null;
                }
            }
            else
            {
                _logger.LogWarning("Output file was not created!");
                return null;
            }
            
            return outputVideoPath;
        }
        else
        {
            _logger.LogError("ffmpeg exited with code {ExitCode}", proc.ExitCode);
            if (!string.IsNullOrWhiteSpace(errorOutput))
            {
                _logger.LogError("ffmpeg stderr: {Error}", errorOutput);
            }
            if (!string.IsNullOrWhiteSpace(standardOutput))
            {
                _logger.LogInformation("ffmpeg stdout: {Output}", standardOutput);
            }
            
            // Try a fallback approach with simpler settings
            _logger.LogInformation("Trying fallback ffmpeg approach...");
            return await TryFallbackVideoCreation(outputVideoPath, fps, cancellationToken);
        }
    }

    private async Task<string?> TryFallbackVideoCreation(string outputVideoPath, int fps, CancellationToken cancellationToken)
    {
        // Simpler ffmpeg command as fallback
        var fallbackArgs = $"-y -r {fps} -pattern_type glob -i \"{OutputDir}/frame_*.jpg\" -c:v libx264 -pix_fmt yuv420p \"{outputVideoPath}\"";
        
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = fallbackArgs,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        
        _logger.LogInformation("Fallback command: ffmpeg {Args}", fallbackArgs);
        
        using var proc = Process.Start(psi);
        if (proc == null)
        {
            _logger.LogError("Failed to start fallback ffmpeg command.");
            return null;
        }
        
        var errorOutput = await proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);
        
        if (proc.ExitCode == 0)
        {
            _logger.LogInformation("Fallback video creation successful!");
            return outputVideoPath;
        }
        else
        {
            _logger.LogError("Fallback ffmpeg also failed with code {ExitCode}", proc.ExitCode);
            if (!string.IsNullOrWhiteSpace(errorOutput))
            {
                _logger.LogError("Fallback ffmpeg stderr: {Error}", errorOutput);
            }
            return null;
        }
    }

    public void Dispose()
    {
        // No unmanaged resources
    }
}
