using System.Diagnostics;
using System.Collections.Generic;
using PrintStreamer.Interfaces;

namespace PrintStreamer.Streamers
{
	class FfmpegStreamer : IStreamer

	{
	private readonly string _source;
	private readonly string _rtmpUrl;
	private readonly int _targetFps;
	private readonly int _bitrateKbps;
	private readonly FfmpegOverlayOptions? _overlay;
	private Process? _proc;
	private TaskCompletionSource<object?>? _exitTcs;

	public Task ExitTask => _exitTcs?.Task ?? Task.CompletedTask;

	public FfmpegStreamer(string source, string rtmpUrl, int targetFps = 30, int bitrateKbps = 800, FfmpegOverlayOptions? overlay = null)
	{
		_source = source;
		_rtmpUrl = rtmpUrl;
		_targetFps = targetFps <= 0 ? 30 : targetFps;
		_bitrateKbps = bitrateKbps <= 0 ? 800 : bitrateKbps;
		_overlay = overlay;
	}

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		_exitTcs = new TaskCompletionSource<object?>();

	// Ensure overlay prerequisites exist before launching ffmpeg
	if (_overlay is not null)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(_overlay.TextFile))
			{
				var tf = _overlay.TextFile;
				var tfDir = Path.GetDirectoryName(tf);
				if (!string.IsNullOrEmpty(tfDir) && !Directory.Exists(tfDir))
				{
					Directory.CreateDirectory(tfDir);
				}
				if (!File.Exists(tf))
				{
					File.WriteAllText(tf, "Starting…", System.Text.Encoding.UTF8);
				}
			}
			if (!string.IsNullOrWhiteSpace(_overlay.FontFile) && !File.Exists(_overlay.FontFile))
			{
				Console.WriteLine($"[Overlay] Warning: Font file not found at '{_overlay.FontFile}'. ffmpeg may fail to render text.");
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Overlay] Preflight error: {ex.Message}");
		}
	}

	var ffmpegArgs = BuildFfmpegArgs(_source, _rtmpUrl, _targetFps, _bitrateKbps, _overlay);

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

	private static string BuildFfmpegArgs(string source, string rtmpUrl, int fps, int bitrateKbps, FfmpegOverlayOptions? overlay)
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

	// ffmpeg logging: allow overriding via env var to hide benign errors
	string logLevel = System.Environment.GetEnvironmentVariable("FFMPEG_LOGLEVEL") ?? "error";
	switch (logLevel.ToLowerInvariant())
	{
		case "quiet": case "panic": case "fatal": case "error": case "warning": case "info": case "verbose": case "debug": case "trace":
			break;
		default:
			logLevel = "error";
			break;
	}
	var baseFlags = $"-hide_banner -nostats -loglevel {logLevel} -nostdin -err_detect ignore_err";

	string inputArgs;
	string mapArgs;
	string audioEncArgs = "";

	if (addSilentAudio)
	{
		// Two inputs: video (0) and synthetic silent audio (1)
		// Use reconnect options and input hints identical to youtube_stream.sh for MJPEG HTTP inputs.
		var reconnectArgs = "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2";
		inputArgs = $"{baseFlags} {reconnectArgs} -fflags +genpts -f mjpeg -use_wallclock_as_timestamps 1 -i \"{srcArg}\" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100";
		// Map video from input 0 and audio from input 1
		mapArgs = "-map 0:v:0 -map 1:a:0";
		audioEncArgs = "-c:a aac -b:a 128k -ar 44100 -ac 2";
	}
	else
	{
		inputArgs = $"{baseFlags} -re {inputFormat}-i \"{srcArg}\"";
		mapArgs = "";
		audioEncArgs = "-an";
	}

	// Add input frame rate hint for better behavior on low-frame-rate sources
	// (we set -r on the output later; no separate fpsArg needed)

	// Final output to rtmp flv, use genpts and flvflags similar to the shell script
	var vfFilters = new List<string>();
	// Ensure pixel format for compatibility
	vfFilters.Add("format=yuv420p");
	// Optional scale to a known baseline; adapt as desired
	vfFilters.Add("scale=640:480");

	// Inject drawtext overlay if configured
	if (overlay is not null && !string.IsNullOrWhiteSpace(overlay.TextFile))
	{
		string esc(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
		var font = esc(overlay.FontFile ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf");
		var text = esc(overlay.TextFile);
		var box = overlay.Box ? 1 : 0;
		var fontColor = string.IsNullOrWhiteSpace(overlay.FontColor) ? "white" : overlay.FontColor;
		var boxColor = string.IsNullOrWhiteSpace(overlay.BoxColor) ? "black@0.4" : overlay.BoxColor;
		var x = string.IsNullOrWhiteSpace(overlay.X) ? "(w-tw)-20" : overlay.X;
		var y = string.IsNullOrWhiteSpace(overlay.Y) ? "20" : overlay.Y;
		var fontsize = overlay.FontSize <= 0 ? 22 : overlay.FontSize;
		var borderw = overlay.BoxBorderW < 0 ? 8 : overlay.BoxBorderW;

		// Draw a full-width background bar behind the text using drawbox
		try
		{
			int lineCount = 1;
			try
			{
				var initialTextPath = overlay!.TextFile;
				var initialText = File.Exists(initialTextPath) ? File.ReadAllText(initialTextPath) : string.Empty;
				if (!string.IsNullOrEmpty(initialText))
				{
					lineCount = initialText.Split('\n').Length;
				}
			}
			catch { }
			int padTop;
			if (!int.TryParse(y, out padTop)) padTop = 20;
			var approxTextHeight = Math.Max(fontsize, 12) * Math.Max(1, lineCount);
			var extra = 6; // small fudge factor for ascent/descent
			var boxH = padTop + approxTextHeight + borderw + extra;
			var drawbox = $"drawbox=x=0:y=0:w=iw:h={boxH}:color={boxColor}:t=fill";
			vfFilters.Add(drawbox);
		}
		catch { }

		// Now draw the text on top (no inner box; drawbox above provides the banner background)
		var draw = $"drawtext=fontfile='{font}':textfile='{text}':reload=1:expansion=none:fontsize={fontsize}:fontcolor={fontColor}:x={x}:y={y}";
		vfFilters.Add(draw);
	}

	var vf = string.Join(",", vfFilters);
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

	public sealed class FfmpegOverlayOptions
	{
	public string TextFile { get; init; } = string.Empty;
	public string FontFile { get; init; } = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf";
	public int FontSize { get; init; } = 22;
	public string FontColor { get; init; } = "white";
	public bool Box { get; init; } = true;
	public string BoxColor { get; init; } = "black@0.4";
	public int BoxBorderW { get; init; } = 8;
	public string X { get; init; } = "(w-tw)-20";
	public string Y { get; init; } = "20";
	}
}
