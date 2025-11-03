using System.Diagnostics;
using PrintStreamer.Interfaces;
using System.Globalization;

namespace PrintStreamer.Streamers
{
	class FfmpegStreamer : IStreamer

	{
		private readonly string _source;
		private readonly string? _audioUrl;
		private readonly string? _rtmpUrl;
		private readonly int _targetFps;
		private readonly int _bitrateKbps;
		private readonly FfmpegOverlayOptions? _overlay;
		private Process? _proc;
		private TaskCompletionSource<object?>? _exitTcs;

		public Task ExitTask => _exitTcs?.Task ?? Task.CompletedTask;

		public FfmpegStreamer(string source, string? rtmpUrl, int targetFps = 30, int bitrateKbps = 2500, FfmpegOverlayOptions? overlay = null, string? audioUrl = null)
		{
			_source = source;
			_rtmpUrl = rtmpUrl;
			_targetFps = targetFps <= 0 ? 30 : targetFps;
			_bitrateKbps = bitrateKbps <= 0 ? 800 : bitrateKbps;
			_overlay = overlay;
			_audioUrl = audioUrl;
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

			var ffmpegArgs = BuildFfmpegArgs(_source, _rtmpUrl, _targetFps, _bitrateKbps, _overlay, _audioUrl);

			Console.WriteLine($"Starting ffmpeg with args: {ffmpegArgs}");

			var psi = new ProcessStartInfo
			{
				FileName = "ffmpeg",
				Arguments = ffmpegArgs,
				UseShellExecute = false,
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				RedirectStandardInput = true,
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
				
				// Improved error handling: suppress benign decode warnings that don't affect stream quality
				var lastBenignWarning = DateTime.MinValue;
				_proc.ErrorDataReceived += (s, e) => 
				{ 
					if (e.Data != null)
					{
						var line = e.Data;
						// Suppress common benign MJPEG warnings that don't indicate real problems
						if (line.Contains("unable to decode APP fields") || 
						    line.Contains("Last message repeated"))
						{
							var now = DateTime.UtcNow;
							if ((now - lastBenignWarning).TotalSeconds > 30)
							{
								Console.WriteLine("[ffmpeg] Suppressing benign MJPEG decode warnings (last 30s)");
								lastBenignWarning = now;
							}
						}
						else
						{
							Console.WriteLine(line);
						}
					}
				};

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
					try
					{
						// send 'q' to request ffmpeg to quit gracefully
						if (_proc.StartInfo.RedirectStandardInput)
						{
							_proc.StandardInput.WriteLine("q");
							_proc.StandardInput.Flush();
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Warning: failed to send quit to ffmpeg stdin: {ex.Message}");
					}
					// wait briefly for ffmpeg to exit on its own
					if (!_proc.WaitForExit(5000))
					{
						Console.WriteLine("ffmpeg did not exit within timeout, killing...");
						_proc.Kill(true);
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error stopping ffmpeg: {ex.Message}");
			}
		}

		private static string BuildFfmpegArgs(string source, string? rtmpUrl, int fps, int bitrateKbps, FfmpegOverlayOptions? overlay, string? audioUrl)
		{
			// Basic ffmpeg args that should work for many MJPEG/http or v4l2 inputs.
			// -re to read input at native frame rate
			// Video: libx264, preset ultrafast for low-latency encoding

			// If source looks like a v4l2 device on Linux, pass -f v4l2
			var srcArg = source;
			var inputFormat = "";
			if (source.StartsWith("/dev/") || source.StartsWith("/dev\\"))
			{
				inputFormat = "-f v4l2 ";
			}

			// Build encoding args tuned for target fps/bitrate
			// If the source is an HTTP MJPEG stream, prefer to let ffmpeg preserve the input frame timing
			// instead of forcing an output frame rate. Only add -r when an explicit target fps is desired.
			// Detect HTTP source early
			var isHttpSource = srcArg.StartsWith("http", StringComparison.OrdinalIgnoreCase);
			int effectiveFps = fps <= 0 ? 0 : fps;
			if (isHttpSource)
			{
				// For HTTP/MJPEG inputs, avoid forcing the output -r so ffmpeg follows input timing
				effectiveFps = 0;
			}
			var gop = Math.Max(2, (effectiveFps > 0 ? effectiveFps * 2 : Math.Max(2, fps * 2)));
			// Use keyint equal to GOP, set pix_fmt and genpts to help timestamping for variable sources
			// (video encoding args constructed later when mux targets are known)

			// For MJPEG/http sources that have no audio, include a silent audio track so YouTube sees audio present.
			// We'll add a lavfi anullsrc input and map it as the audio input.
			var addSilentAudio = string.IsNullOrWhiteSpace(audioUrl) && (isHttpSource || srcArg.StartsWith("/dev/") == false);

			// ffmpeg logging: allow overriding via env var to hide benign errors
			string logLevel = System.Environment.GetEnvironmentVariable("FFMPEG_LOGLEVEL") ?? "error";
			switch (logLevel.ToLowerInvariant())
			{
				case "quiet":
				case "panic":
				case "fatal":
				case "error":
				case "warning":
				case "info":
				case "verbose":
				case "debug":
				case "trace":
					break;
				default:
					logLevel = "error";
					break;
			}
			var baseFlags = $"-hide_banner -nostats -loglevel {logLevel} -nostdin -err_detect ignore_err";

			string inputArgs;
			string audioEncArgs = "";

			string audioMap = "";
			if (!string.IsNullOrWhiteSpace(audioUrl))
			{
				// Video input + HTTP MP3 audio input from API endpoint
				// Increase reconnect attempts and use analyzeduration/probesize to handle stream issues better
				var reconnectArgs = "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2 -reconnect_on_network_error 1";
				var audioReconnect = "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2 -reconnect_on_network_error 1";
				// Add analyzeduration and probesize to handle variable/unstable streams
				// Set max_delay to help with sync recovery after input issues
				inputArgs = $"{baseFlags} {reconnectArgs} -fflags +genpts+discardcorrupt -analyzeduration 5M -probesize 10M -max_delay 5000000 -f mjpeg -use_wallclock_as_timestamps 1 -i \"{srcArg}\" {audioReconnect} -fflags +genpts+discardcorrupt -analyzeduration 2M -probesize 5M -i \"{audioUrl}\"";
				// Map audio from second input
				audioMap = "-map 1:a:0";
				audioEncArgs = "-c:a aac -b:a 128k -ar 44100 -ac 2";
			}
			else if (addSilentAudio)
			{
				// Add reconnect logic for HTTP sources and add a silent audio track
				// Increase reconnect attempts and use analyzeduration/probesize to handle stream issues better
				var reconnectArgs = "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2 -reconnect_on_network_error 1";
				// Add fflags to discard corrupted frames and continue processing
				inputArgs = $"{baseFlags} {reconnectArgs} -fflags +genpts+discardcorrupt -analyzeduration 5M -probesize 10M -max_delay 5000000 -f mjpeg -use_wallclock_as_timestamps 1 -i \"{srcArg}\" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100";
				// audio is the second input
				audioMap = "-map 1:a:0";
				audioEncArgs = "-c:a aac -b:a 128k -ar 44100 -ac 2";
			}
			else
			{
				inputArgs = $"{baseFlags} -re {inputFormat}-i \"{srcArg}\"";
				audioMap = "";
				audioEncArgs = "-an";
			}



			// Build video filter chain and use filter_complex so the filtered video stream can be mapped
			var vfFilters = new List<string>();
			// Ensure pixel format for compatibility
			vfFilters.Add("format=yuv420p");
			// NOTE: removed forced scaling to 640x480 so the encoder preserves the source
			// resolution by default. If you want to enforce a target resolution, add a
			// configurable scale filter here (e.g. scale=1280:-1 for 720p/1080p targeting).

			// Inject drawtext overlay if configured
			if (overlay is not null && !string.IsNullOrWhiteSpace(overlay.TextFile))
			{
				string esc(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
				var font = esc(overlay.FontFile ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf");
				var text = esc(overlay.TextFile);
				var fontColor = string.IsNullOrWhiteSpace(overlay.FontColor) ? "white" : overlay.FontColor;
				var boxColor = string.IsNullOrWhiteSpace(overlay.BoxColor) ? "black@0.4" : overlay.BoxColor;
				var x = "0";
				// Keep the raw overlay.Y value (don't default to 20 here) so we can decide
				// whether to honor an explicit config or compute a bottom-anchored value.
				var y = overlay.Y;
				var fontsize = overlay.FontSize <= 0 ? 22 : overlay.FontSize;
				var borderw = overlay.BoxBorderW < 0 ? 8 : overlay.BoxBorderW;

				// Draw a full-width background bar behind the text using drawbox
				try
				{
					// Estimate the text height from the current text file so the banner can be
					// sized to the text and anchored to the bottom. This is an approximation
					// (based on fontsize and line count) and will be recomputed when ffmpeg
					// is restarted or the initial file is present at startup.
					int lineCount = 1;
					try
					{
						var initialTextPath = overlay.TextFile;
						var initialText = File.Exists(initialTextPath) ? File.ReadAllText(initialTextPath) : string.Empty;
						if (!string.IsNullOrEmpty(initialText))
						{
							lineCount = initialText.Split('\n').Length;
						}
					}
					catch { }

					var approxTextHeight = Math.Max(fontsize, 12) * Math.Max(1, lineCount);
					var padding = 32; // top+bottom padding approx
					var extra = 6; // small fudge for ascent/descent
					var boxH = padding + approxTextHeight + borderw + extra;

					// Place drawbox anchored to the bottom: y = h - boxH
					// Use parentheses around the numeric box height to avoid any parsing ambiguity
					var drawbox = $"drawbox=x=0:y=ih-({boxH}):w=iw:h={boxH}:color={boxColor}:t=fill";
					vfFilters.Add(drawbox);

					// Compute a default Y value that places the text inside the banner if overlay.Y not provided
					// or if it appears to be the old default ("20"). Treat some legacy defaults as unset.
					var textY = $"h-({boxH})+{padding / 2}";
					var textX = $"{x} + {padding / 2}";
					var draw = $"drawtext=fontfile='{font}':textfile='{text}':reload=1:expansion=none:fontsize={fontsize}:fontcolor={fontColor}:x={textX}:y={textY}";
					vfFilters.Add(draw);
				}
				catch { }
			}

			var vfChain = string.Join(",", vfFilters);
			// Build filter_complex for a single labelled output [vout]
			string filterComplex = $"-filter_complex \"[0:v]{vfChain}[vout]\"";
			var colorRange = "-color_range tv";
			var profile = "-profile:v high -level 4.1";
			var flvFlags = "-flvflags no_duration_filesize";

			// Common encoding args for video
			// Only include -r when effectiveFps > 0; otherwise don't force output framerate and let ffmpeg use input timing.
			var rArg = effectiveFps > 0 ? $"-r {effectiveFps} " : string.Empty;
			// Use a faster preset suitable for streaming but with better quality than ultrafast
			// Enforce consistent keyframe behavior for YouTube: 2s GOP, no scenecut, and force keyframes every 2s
			var x264Params = $"-x264-params \"keyint={gop}:min-keyint={gop}:scenecut=0\"";
			var forceKf = $"-force_key_frames \"expr:gte(t,n_forced*2)\"";
			var videoEnc = $"-c:v libx264 -preset ultrafast -tune zerolatency {profile} -pix_fmt yuv420p {rArg}-g {gop} -keyint_min {gop} -sc_threshold 0 {x264Params} {forceKf} -b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k";


			// Build the final command so both outputs explicitly map the filtered [vout] and audio map.
			// Place filter_complex before outputs and then provide per-output mapping and muxer options.
			var cmd = new System.Text.StringBuilder();
			cmd.Append(inputArgs);
			cmd.Append(' ');
			cmd.Append(filterComplex);
			cmd.Append(' ');
			// Add muxer/timestamp safeties to avoid non-monotonic DTS when inputs have unstable or missing timestamps.
			// -avoid_negative_ts make_zero: clamp negative timestamps to zero
			// -fflags +genpts: regenerate presentation timestamps where missing
			// -vsync 2: variable frame-rate handling (vfr) to avoid forcing duplicate/lost frames
			cmd.Append("-avoid_negative_ts make_zero -fflags +genpts -vsync 2 ");
			cmd.Append(colorRange);
			cmd.Append(' ');

			// Only RTMP requested -> map [vout] to RTMP/flv output
			if (!string.IsNullOrWhiteSpace(rtmpUrl))
			{
				// For RTMP output, explicitly set live mode to help some endpoints
				cmd.Append($"-map [vout] {videoEnc} {audioEncArgs} {flvFlags} {audioMap} -f flv -rtmp_live live {rtmpUrl}");
				return cmd.ToString();
			}

			// No RTMP requested: drop to null sink (no output generated)
			cmd.Append($"-map [vout] {videoEnc} {audioEncArgs} {audioMap} -f null -");
			return cmd.ToString();
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
		public string Y { get; init; } = string.Empty;
		// Fraction of the frame height used by the bottom banner (0.0 - 1.0). Default 0.2 -> 20%.
		public double BannerFraction { get; init; } = 0.2;
	}
}
