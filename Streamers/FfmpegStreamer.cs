using System.Diagnostics;
using PrintStreamer.Interfaces;
using System.Globalization;
using Microsoft.Extensions.Logging;

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
		private readonly ILogger<FfmpegStreamer> _logger;
		private readonly string _contextLabel;
		private Process? _proc;
		private TaskCompletionSource<object?>? _exitTcs;

		public Task ExitTask => _exitTcs?.Task ?? Task.CompletedTask;

		public FfmpegStreamer(string source, string? rtmpUrl, int targetFps, int bitrateKbps, FfmpegOverlayOptions? overlay, string? audioUrl, ILogger<FfmpegStreamer> logger)
		{
			_source = source;
			_rtmpUrl = rtmpUrl;
			_targetFps = targetFps <= 0 ? 30 : targetFps;
			_bitrateKbps = bitrateKbps <= 0 ? 800 : bitrateKbps;
			_overlay = overlay;
			_audioUrl = audioUrl;
			_logger = logger;
			
			// Build a descriptive label for logging context
			if (!string.IsNullOrWhiteSpace(rtmpUrl))
			{
				_contextLabel = "YouTube RTMP Output";
			}
			else if (!string.IsNullOrWhiteSpace(audioUrl))
			{
				_contextLabel = "Local Stream (with Audio)";
			}
			else
			{
				_contextLabel = "Local Stream (Silent)";
			}
		}

		public Task StartAsync(CancellationToken cancellationToken = default)
		{
			_exitTcs = new TaskCompletionSource<object?>();

			// Note: Overlay text is now handled upstream by the /stream/overlay endpoint.
			// FfmpegStreamer no longer manages overlay text files or fonts.

			var ffmpegArgs = BuildFfmpegArgs(_source, _rtmpUrl, _targetFps, _bitrateKbps, _overlay, _audioUrl);

			_logger.LogInformation("[{ContextLabel}] Starting ffmpeg with args: {Args}", _contextLabel, ffmpegArgs);

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
					_logger.LogInformation("[{ContextLabel}] ffmpeg exited with code {ExitCode}", _contextLabel, _proc?.ExitCode);
					_exitTcs.TrySetResult(null);
				};

				_proc.OutputDataReceived += (s, e) => { if (e.Data != null) _logger.LogDebug("[{ContextLabel}] [ffmpeg stdout] {Data}", _contextLabel, e.Data); };
				
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
								_logger.LogDebug("[{ContextLabel}] Suppressing benign MJPEG decode warnings (last 30s)", _contextLabel);
								lastBenignWarning = now;
							}
						}
						// Suppress benign audio frame errors too
						else if (line.Contains("Header missing") || line.Contains("Error while decoding stream"))
						{
							var now = DateTime.UtcNow;
							if ((now - lastBenignWarning).TotalSeconds > 30)
							{
								_logger.LogDebug("[{ContextLabel}] Audio frame decode issue (continuing), line: {Line}", _contextLabel, line);
								lastBenignWarning = now;
							}
						}
						else
						{
							_logger.LogWarning("[{ContextLabel}] [ffmpeg stderr] {Data}", _contextLabel, line);
						}
					}
				};

				if (!_proc.Start())
				{
					_logger.LogError("[{ContextLabel}] Failed to start ffmpeg process", _contextLabel);
					_exitTcs.TrySetResult(null);
					return _exitTcs.Task;
				}

				_proc.BeginOutputReadLine();
				_proc.BeginErrorReadLine();

				_logger.LogInformation("[{ContextLabel}] Streaming... press Ctrl+C to stop", _contextLabel);

				// Handle cancellation
				cancellationToken.Register(() =>
				{
					Stop();
				});
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[{ContextLabel}] Error starting ffmpeg", _contextLabel);
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
					_logger.LogInformation("[{ContextLabel}] Stopping ffmpeg...", _contextLabel);
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
						_logger.LogWarning(ex, "[{ContextLabel}] Warning: failed to send quit to ffmpeg stdin", _contextLabel);
					}
					// wait briefly for ffmpeg to exit on its own (increased from 5s to 15s for better cleanup)
					if (!_proc.WaitForExit(15000))
					{
						_logger.LogWarning("[{ContextLabel}] ffmpeg did not exit within timeout, killing...", _contextLabel);
						_proc.Kill(true);
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[{ContextLabel}] Error stopping ffmpeg", _contextLabel);
			}
		}

	private static string BuildFfmpegArgs(string source, string? rtmpUrl, int fps, int bitrateKbps, FfmpegOverlayOptions? overlay, string? audioUrl)
	{
		// Check if source is the pre-mixed /stream/mix endpoint which already contains video+audio
		var isPreMixed = source.Contains("/stream/mix", StringComparison.OrdinalIgnoreCase);
		
		if (isPreMixed)
		{
			// Source is already mixed video+audio from MixStreamer
			// Just read it and optionally re-encode or pass-through for RTMP output
			var mixSource = source;
			var mixReconnectArgs = "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2";
			var mixBaseFlags = $"-hide_banner -nostats -loglevel error -nostdin -err_detect ignore_err";
			
			// Input: already mixed MP4 with video+audio
			var mixInputArgs = $"{mixBaseFlags} {mixReconnectArgs} -i \"{mixSource}\"";
			
			// Video encoding - pass through or light re-encode
			int mixEffectiveFps = fps <= 0 ? 30 : fps;
			var mixGop = Math.Max(2, (mixEffectiveFps > 0 ? mixEffectiveFps * 2 : 60));
			var mixRArg = mixEffectiveFps > 0 ? $"-r {mixEffectiveFps} " : string.Empty;
			var mixProfile = "-profile:v high -level 4.1";
			var mixVideoEnc = $"-c:v libx264 -preset ultrafast -tune zerolatency {mixProfile} -pix_fmt yuv420p {mixRArg}-g {mixGop} -b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k";
			
			// Audio encoding - already AAC, just encode/pass
			var mixAudioEnc = "-c:a aac -b:a 128k -ar 44100";
			
			// Build output
			var mixCmd = new System.Text.StringBuilder();
			mixCmd.Append(mixInputArgs);
			mixCmd.Append(" -avoid_negative_ts make_zero -fflags +genpts -vsync 1 ");
			
			if (!string.IsNullOrWhiteSpace(rtmpUrl))
			{
				// Output to RTMP for YouTube
				var mixFlvFlags = "-flvflags no_duration_filesize";
				mixCmd.Append($"-map 0:v -map 0:a {mixVideoEnc} {mixAudioEnc} {mixFlvFlags} -f flv -rtmp_live live \"{rtmpUrl}\"");
				return mixCmd.ToString();
			}
			else
			{
				// No output
				mixCmd.Append($"-map 0:v -map 0:a {mixVideoEnc} {mixAudioEnc} -f null -");
				return mixCmd.ToString();
			}
		}

		// Original logic for non-mixed sources (backward compatibility)			// If source looks like a v4l2 device on Linux, pass -f v4l2
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
			// Enforce a constant output framerate for stability with live RTMP (prevents drift/creep)
			int effectiveFps = fps <= 0 ? 30 : fps;
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
				var audioReconnect = "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -reconnect_on_network_error 1";
				// Add analyzeduration and probesize to handle variable/unstable streams
				// Set max_delay to help with sync recovery after input issues
				// Add -read_timeout for audio input to prevent hangs on broken streams
				inputArgs = $"{baseFlags} {reconnectArgs} -re -thread_queue_size 1024 -fflags +genpts+discardcorrupt -analyzeduration 5M -probesize 10M -max_delay 5000000 -f mjpeg -use_wallclock_as_timestamps 1 -i \"{srcArg}\" {audioReconnect} -re -thread_queue_size 1024 -fflags +genpts+discardcorrupt -analyzeduration 2M -probesize 5M -rw_timeout 10000000 -i \"{audioUrl}\"";
				// Map audio from second input (but ffmpeg will gracefully continue if audio fails)
				audioMap = "-map 1:a:0";
				audioEncArgs = "-c:a aac -b:a 128k -ar 44100 -ac 2";
			}
			else if (addSilentAudio)
			{
				// Add reconnect logic for HTTP sources and add a silent audio track
				// Increase reconnect attempts and use analyzeduration/probesize to handle stream issues better
				var reconnectArgs = "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2 -reconnect_on_network_error 1";
				// Add fflags to discard corrupted frames and continue processing
				inputArgs = $"{baseFlags} {reconnectArgs} -re -thread_queue_size 1024 -fflags +genpts+discardcorrupt -analyzeduration 5M -probesize 10M -max_delay 5000000 -f mjpeg -use_wallclock_as_timestamps 1 -i \"{srcArg}\" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100";
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

			// No drawtext/drawbox here: the video already includes overlay from /stream/overlay

			// No additional scaling here; the overlay pipeline outputs 1080p already.

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
			cmd.Append("-avoid_negative_ts make_zero -fflags +genpts -vsync 1 -max_interleave_delta 1000000 -muxpreload 0 -muxdelay 0 -max_muxing_queue_size 1024 ");
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
