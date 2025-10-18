using System.Diagnostics;

class FfmpegStreamer : IStreamer

{
	private readonly string _source;
	private readonly string _rtmpUrl;
	private readonly int _targetFps;
	private readonly int _bitrateKbps;
	private Process? _proc;
	private TaskCompletionSource<object?>? _exitTcs;

	public Task ExitTask => _exitTcs?.Task ?? Task.CompletedTask;

	public FfmpegStreamer(string source, string rtmpUrl, int targetFps = 6, int bitrateKbps = 800)
	{
		_source = source;
		_rtmpUrl = rtmpUrl;
		_targetFps = targetFps <= 0 ? 6 : targetFps;
		_bitrateKbps = bitrateKbps <= 0 ? 800 : bitrateKbps;
	}

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		_exitTcs = new TaskCompletionSource<object?>();

	var ffmpegArgs = BuildFfmpegArgs(_source, _rtmpUrl, _targetFps, _bitrateKbps);

		Console.WriteLine($"Starting ffmpeg with args: {ffmpegArgs}");

		var psi = new ProcessStartInfo
		{
			FileName = "ffmpeg",
			Arguments = ffmpegArgs,
			UseShellExecute = false,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			CreateNoWindow = true,
		};

		try
		{
			_proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
			_proc.Exited += (s, e) =>
			{
				Console.WriteLine($"ffmpeg exited with code {_proc?.ExitCode}");
				_exitTcs.TrySetResult(null);
			};

			_proc.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
			_proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };

			if (!_proc.Start())
			{
				Console.WriteLine("Failed to start ffmpeg process.");
				_exitTcs.TrySetResult(null);
				return _exitTcs.Task;
			}

			_proc.BeginOutputReadLine();
			_proc.BeginErrorReadLine();

			Console.WriteLine("Streaming... press Ctrl+C to stop.");
			
			// Handle cancellation
			cancellationToken.Register(() =>
			{
				Stop();
			});
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Error starting ffmpeg: {ex.Message}");
			_exitTcs.TrySetResult(null);
		}

		return _exitTcs.Task;
	}

	public void Stop()
	{
		try
		{
			if (_proc != null && !_proc.HasExited)
			{
				Console.WriteLine("Stopping ffmpeg...");
				// request graceful stop
				_proc.Kill(true);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Error stopping ffmpeg: {ex.Message}");
		}
	}

	private static string BuildFfmpegArgs(string source, string rtmpUrl, int fps, int bitrateKbps)
	{
		// Basic ffmpeg args that should work for many MJPEG/http or v4l2 inputs.
		// -re to read input at native frame rate
		// Video: libx264, preset veryfast for low-latency encoding
		// Audio: disabled by default (YouTube expects audio); we disable audio to avoid added complexity.

		// If source looks like a v4l2 device on Linux, pass -f v4l2
		var srcArg = source;
		var inputFormat = "";
		if (source.StartsWith("/dev/") || source.StartsWith("/dev\\"))
		{
			inputFormat = "-f v4l2 ";
		}

	// Build encoding args tuned for target fps/bitrate
	var gop = Math.Max(2, fps * 2);
	var encArgs = $"-c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p -g {gop} -b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k";

		// Disable audio
		var audioArgs = "-an";

		// Final output to rtmp flv
		var outArgs = $"-f flv {rtmpUrl}";

		// Add input frame rate hint for better behavior on low-frame-rate sources
		var fpsArg = $"-r {fps}";

		return $"-re {inputFormat}-i \"{srcArg}\" {fpsArg} {encArgs} {audioArgs} {outArgs}";
	}

	public void Dispose()
	{
		Stop();
		_proc?.Dispose();
	}
}
