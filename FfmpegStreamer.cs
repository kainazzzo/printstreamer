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

	public FfmpegStreamer(string source, string rtmpUrl, int targetFps = 30, int bitrateKbps = 800)
	{
		_source = source;
		_rtmpUrl = rtmpUrl;
		_targetFps = targetFps <= 0 ? 30 : targetFps;
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
	// Use keyint equal to GOP, set pix_fmt and genpts to help timestamping for variable sources
	var videoEncArgs = $"-c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p -g {gop} -keyint_min {gop} -b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k";

	// For MJPEG/http sources that have no audio, include a silent audio track so YouTube sees audio present.
	// We'll add a lavfi anullsrc input and map it as the audio input.
	var isHttpSource = srcArg.StartsWith("http", StringComparison.OrdinalIgnoreCase);
	var addSilentAudio = isHttpSource || srcArg.StartsWith("/dev/") == false;

	string inputArgs;
	string mapArgs;
	string audioEncArgs = "";

	if (addSilentAudio)
	{
		// Two inputs: video (0) and synthetic silent audio (1)
		// Use reconnect options and input hints identical to youtube_stream.sh for MJPEG HTTP inputs.
		var reconnectArgs = "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2";
		inputArgs = $"-hide_banner -loglevel error -nostdin -err_detect ignore_err {reconnectArgs} -fflags +genpts -f mjpeg -use_wallclock_as_timestamps 1 -i \"{srcArg}\" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100";
		// Map video from input 0 and audio from input 1
		mapArgs = "-map 0:v:0 -map 1:a:0";
		audioEncArgs = "-c:a aac -b:a 128k -ar 44100 -ac 2";
	}
	else
	{
		inputArgs = $"-re {inputFormat}-i \"{srcArg}\"";
		mapArgs = "";
		audioEncArgs = "-an";
	}

	// Add input frame rate hint for better behavior on low-frame-rate sources
	// (we set -r on the output later; no separate fpsArg needed)

	// Final output to rtmp flv, use genpts and flvflags similar to the shell script
	var vf = "format=yuv420p,scale=640:480";
	var colorRange = "-color_range tv";
	var profile = "-profile:v baseline";
	var flvFlags = "-flvflags no_duration_filesize";

	// Use the same ordering and flags as the shell script
	var outArgs = $"-vf \"{vf}\" {colorRange} -c:v libx264 -preset veryfast -tune zerolatency {profile} -pix_fmt yuv420p -r {fps} -g {gop} -keyint_min {gop} -b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k {audioEncArgs} {flvFlags} -f flv {rtmpUrl}";

	// Prepend input args (which include -re for MJPEG input), include mapping, and return
	return $"{inputArgs} {mapArgs} {outArgs}";
	}

	public void Dispose()
	{
		Stop();
		_proc?.Dispose();
	}
}
