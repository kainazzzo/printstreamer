using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class TimelapseService : IDisposable
{
    public string OutputDir { get; }
    private int _frameCount = 0;
    private bool _finalized = false;

    public TimelapseService(string mainFolder, string streamId)
    {
        // mainFolder: base timelapse directory (configurable)
        // streamId: unique per stream/job (timestamp, job name, etc)
        OutputDir = Path.Combine(mainFolder, streamId);
        Directory.CreateDirectory(OutputDir);
    }

    public async Task SaveFrameAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        var filename = Path.Combine(OutputDir, $"frame_{_frameCount:D6}.png");
        await File.WriteAllBytesAsync(filename, imageBytes, cancellationToken);
        Interlocked.Increment(ref _frameCount);
    }

    public async Task<string?> CreateVideoAsync(string outputVideoPath, int fps = 30, CancellationToken cancellationToken = default)
    {
        if (_finalized) return null;
        _finalized = true;
        // Use ffmpeg to create video from images
    // Example: ffmpeg -framerate 30 -i frame_%06d.png -c:v libx264 -pix_fmt yuv420p output.mp4
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -framerate {fps} -i {OutputDir}/frame_%06d.png -c:v libx264 -pix_fmt yuv420p {outputVideoPath}",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null)
        {
            Console.WriteLine("Failed to start ffmpeg for timelapse.");
            return null;
        }
        // Optionally read output for logging
        await proc.WaitForExitAsync(cancellationToken);
        if (proc.ExitCode == 0)
        {
            Console.WriteLine($"Timelapse video created: {outputVideoPath}");
            return outputVideoPath;
        }
        else
        {
            Console.WriteLine($"ffmpeg exited with code {proc.ExitCode}");
            return null;
        }
    }

    public void Dispose()
    {
        // No unmanaged resources
    }
}
