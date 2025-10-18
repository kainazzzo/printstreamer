using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

/// <summary>
/// Native .NET MJPEG to RTMP streamer that processes frames without external ffmpeg dependency.
/// Reads MJPEG stream, extracts JPEG frames, and streams to RTMP destination.
/// </summary>
internal class MjpegToRtmpStreamer : IStreamer
{
	private readonly string _sourceUrl;
	private readonly string _rtmpUrl;
	private readonly HttpClient _httpClient;
	private readonly TaskCompletionSource<object?> _exitTcs;
	private CancellationTokenSource? _cts;
	private Task? _streamTask;
	private readonly Channel<byte[]> _frameChannel;
	private readonly int _targetFps;
	private readonly int _bitrateKbps;

	public Task ExitTask => _exitTcs.Task;

	public MjpegToRtmpStreamer(string sourceUrl, string rtmpUrl, int targetFps = 6, int bitrateKbps = 800, int maxQueue = 8)
	{
		_sourceUrl = sourceUrl;
		_rtmpUrl = rtmpUrl;
		_httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		_exitTcs = new TaskCompletionSource<object?>();
		_targetFps = targetFps <= 0 ? 6 : targetFps;
		_bitrateKbps = bitrateKbps <= 0 ? 800 : bitrateKbps;
		var options = new BoundedChannelOptions(maxQueue > 0 ? maxQueue : 8)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true,
			SingleWriter = false
		};
		_frameChannel = Channel.CreateBounded<byte[]>(options);
	}

	/// <summary>
	/// Start streaming from MJPEG source to RTMP destination.
	/// </summary>
	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		
		_streamTask = Task.Run(async () =>
		{
			try
			{
				// Start producer and consumer
				var producer = Task.Run(() => StreamProducerAsync(_cts.Token), _cts.Token);
				var consumer = Task.Run(() => StreamConsumerAsync(_cts.Token), _cts.Token);

				await Task.WhenAll(producer, consumer);
			}
			catch (OperationCanceledException)
			{
				Console.WriteLine("Stream canceled.");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Stream error: {ex.Message}");
			}
			finally
			{
				_exitTcs.TrySetResult(null);
			}
		}, _cts.Token);

		return _streamTask;
	}

	/// <summary>
	/// Stop the streaming process.
	/// </summary>
	public void Stop()
	{
		_cts?.Cancel();
	}

	private async Task StreamProducerAsync(CancellationToken cancellationToken)
	{
		Console.WriteLine($"Connecting to MJPEG source: {_sourceUrl}");

		using var request = new HttpRequestMessage(HttpMethod.Get, _sourceUrl);
		using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		response.EnsureSuccessStatusCode();

		var contentType = response.Content.Headers.ContentType?.ToString();
		Console.WriteLine($"Source content-type: {contentType}");

		if (contentType == null || !contentType.Contains("multipart/x-mixed-replace"))
		{
			throw new InvalidOperationException($"Source is not an MJPEG stream. Content-Type: {contentType}");
		}

		// Extract boundary from content-type header
		string? boundary = null;
		if (contentType.Contains("boundary="))
		{
			var parts = contentType.Split(new[] { "boundary=" }, StringSplitOptions.None);
			if (parts.Length > 1)
			{
				boundary = parts[1].Trim('"', ' ', '\r', '\n');
			}
		}

		Console.WriteLine($"MJPEG boundary: {boundary ?? "(auto-detect)"}");

		using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

		Console.WriteLine("Producer connected to source, extracting frames...");

		// Read and extract frames, pushing them into the bounded channel
		var frameExtractor = new MjpegFrameExtractor(boundary);
		var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
				if (bytesRead == 0) break;

				frameExtractor.AppendData(buffer.AsSpan(0, bytesRead));

				while (frameExtractor.TryExtractFrame(out var frameData))
				{
					// Try to write to channel; if full, older frames are dropped by policy
					await _frameChannel.Writer.WriteAsync(frameData.ToArray(), cancellationToken);
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
			_frameChannel.Writer.Complete();
		}
	}

	private async Task StreamConsumerAsync(CancellationToken cancellationToken)
	{
		// Initialize RTMP connection and encoder (with simple reconnect logic)
		RtmpConnection? rtmpConnection = null;
		try
		{
			rtmpConnection = new RtmpConnection(_rtmpUrl, _targetFps, _bitrateKbps);
			await rtmpConnection.ConnectAsync(cancellationToken);
			Console.WriteLine($"Consumer started: targetFps={_targetFps}, bitrate={_bitrateKbps}kbps");

			var minFrameInterval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
			DateTime lastSent = DateTime.MinValue;
			int sent = 0;

			await foreach (var frame in _frameChannel.Reader.ReadAllAsync(cancellationToken))
			{
				var now = DateTime.UtcNow;
				var since = now - lastSent;
				if (since < minFrameInterval)
				{
					var wait = minFrameInterval - since;
					await Task.Delay(wait, cancellationToken);
				}

				// Attempt to send with a small reconnect/retry loop for transient failures
				const int maxSendAttempts = 3;
				int attempt = 0;
				bool sentOk = false;
				while (attempt < maxSendAttempts && !sentOk && !cancellationToken.IsCancellationRequested)
				{
					attempt++;
					try
					{
						if (rtmpConnection == null)
						{
							rtmpConnection = new RtmpConnection(_rtmpUrl, _targetFps, _bitrateKbps);
							await rtmpConnection.ConnectAsync(cancellationToken);
						}
						await rtmpConnection.SendFrameAsync(frame, cancellationToken);
						sentOk = true;
						lastSent = DateTime.UtcNow;
						sent++;
						if (sent % 30 == 0) Console.WriteLine($"Sent frames: {sent}");
					}
					catch (OperationCanceledException) { throw; }
					catch (Exception ex)
					{
						// Log full exception for diagnostics
						Console.WriteLine($"Error sending frame (attempt {attempt}/{maxSendAttempts}): {ex}");
						// Dispose and null out connection so we recreate it on next attempt
						try { rtmpConnection?.Dispose(); } catch { }
						rtmpConnection = null;
						// brief backoff before retrying
						await Task.Delay(1500, cancellationToken);
					}
				}

				if (!sentOk)
				{
					Console.WriteLine("Failed to send frame after retries — aborting consumer.");
					break; // exit the frame loop and stop the streamer
				}
			}

			Console.WriteLine($"Consumer finished, total sent: {sent}");
		}
		finally
		{
			try { rtmpConnection?.Dispose(); } catch { }
		}
	}

	public void Dispose()
	{
		_cts?.Cancel();
		_cts?.Dispose();
		_httpClient?.Dispose();
	}
}

/// <summary>
/// Extracts JPEG frames from an MJPEG stream using boundary detection or JPEG markers.
/// </summary>
internal class MjpegFrameExtractor
{
	private readonly string? _boundary;
	private readonly MemoryStream _buffer;
	private static ReadOnlySpan<byte> JpegSoi => new byte[] { 0xFF, 0xD8 };
	private static ReadOnlySpan<byte> JpegEoi => new byte[] { 0xFF, 0xD9 };

	public MjpegFrameExtractor(string? boundary = null)
	{
		_boundary = boundary;
		_buffer = new MemoryStream();
	}

	public void AppendData(ReadOnlySpan<byte> data)
	{
		_buffer.Write(data);
	}

	public bool TryExtractFrame(out ReadOnlyMemory<byte> frameData)
	{
		frameData = ReadOnlyMemory<byte>.Empty;
		var bufferData = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Position);

		// Look for JPEG start-of-image marker (0xFF 0xD8)
		int soiIndex = FindPattern(bufferData, JpegSoi);
		if (soiIndex == -1)
		{
			return false;
		}

		// Look for JPEG end-of-image marker (0xFF 0xD9) after SOI
		int eoiIndex = FindPattern(bufferData.Slice(soiIndex + 2), JpegEoi);
		if (eoiIndex == -1)
		{
			return false;
		}

		// Adjust EOI index to be relative to buffer start
		eoiIndex = soiIndex + 2 + eoiIndex + 2; // +2 for the marker itself

		// Extract the frame
		var frameLength = eoiIndex - soiIndex;
		var frame = new byte[frameLength];
		Array.Copy(_buffer.GetBuffer(), soiIndex, frame, 0, frameLength);
		frameData = frame;

		// Remove consumed data from buffer
		var remaining = (int)_buffer.Position - eoiIndex;
		if (remaining > 0)
		{
			var remainingData = new byte[remaining];
			Array.Copy(_buffer.GetBuffer(), eoiIndex, remainingData, 0, remaining);
			_buffer.SetLength(0);
			_buffer.Position = 0;
			_buffer.Write(remainingData);
		}
		else
		{
			_buffer.SetLength(0);
			_buffer.Position = 0;
		}

		return true;
	}

	private static int FindPattern(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern)
	{
		for (int i = 0; i <= data.Length - pattern.Length; i++)
		{
			if (data.Slice(i, pattern.Length).SequenceEqual(pattern))
			{
				return i;
			}
		}
		return -1;
	}
}

/// <summary>
/// RTMP connection handler for sending video frames.
/// This is a simplified placeholder - a full RTMP implementation would require
/// proper handshake, AMF encoding, and FLV packet formatting.
/// 
/// For production use, consider using a library like:
/// - FFmpeg.AutoGen (FFmpeg bindings for .NET)
/// - SIPSorcery (has RTMP support)
/// - Or shell out to ffmpeg for encoding only
/// </summary>
internal class RtmpConnection : IDisposable
{
	private readonly string _rtmpUrl;
	private readonly int _fps;
	private readonly int _bitrateKbps;
	private System.Diagnostics.Process? _ffmpegProcess;
	private Stream? _ffmpegInputStream;
	// Keep recent ffmpeg stderr lines in memory for diagnostics
	private readonly ConcurrentQueue<string> _ffmpegStderr = new ConcurrentQueue<string>();
	private const int _ffmpegStderrMax = 100;

	public RtmpConnection(string rtmpUrl, int fps = 30, int bitrateKbps = 800)
	{
		_rtmpUrl = rtmpUrl;
		_fps = fps <= 0 ? 30 : fps;
		_bitrateKbps = bitrateKbps <= 0 ? 800 : bitrateKbps;
	}

	public async Task ConnectAsync(CancellationToken cancellationToken)
	{
		Console.WriteLine($"Initializing RTMP connection to {_rtmpUrl}...");

		// For now, we'll use ffmpeg as an encoder bridge:
		// - Read MJPEG frames in .NET (already done)
		// - Pipe them to ffmpeg stdin as individual JPEGs
		// - ffmpeg re-encodes to H.264 and streams RTMP
		
		// This gives us native frame control while still using ffmpeg for encoding
		var psi = new System.Diagnostics.ProcessStartInfo
		{
			FileName = "ffmpeg",
			Arguments = BuildFfmpegPipeArgs(_rtmpUrl),
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			CreateNoWindow = true
		};

		_ffmpegProcess = new System.Diagnostics.Process { StartInfo = psi };
		_ffmpegProcess.ErrorDataReceived += (s, e) => 
		{ 
			if (!string.IsNullOrEmpty(e.Data))
			{
				// store recent stderr lines
				_ffmpegStderr.Enqueue(e.Data);
				while (_ffmpegStderr.Count > _ffmpegStderrMax && _ffmpegStderr.TryDequeue(out _)) { }

				if (e.Data.Contains("frame="))
				{
					// Only log every ~100 frames to avoid spam
					if (e.Data.Contains("frame= ") && int.TryParse(e.Data.Split("frame=")[1].Trim().Split(' ')[0], out var frameNum) && frameNum % 100 == 0)
					{
						Console.WriteLine($"[ffmpeg] {e.Data.Trim()}");
					}
				}
			}
		};

		if (!_ffmpegProcess.Start())
		{
			throw new InvalidOperationException("Failed to start ffmpeg process");
		}

		_ffmpegProcess.BeginErrorReadLine();
		_ffmpegInputStream = _ffmpegProcess.StandardInput.BaseStream;

		Console.WriteLine("RTMP connection established via ffmpeg pipe.");
		await Task.CompletedTask;
	}

	public async Task SendFrameAsync(ReadOnlyMemory<byte> jpegData, CancellationToken cancellationToken)
	{
		if (_ffmpegInputStream == null)
		{
			throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
		}

			try
			{
				// Write JPEG frame directly to ffmpeg stdin
				await _ffmpegInputStream.WriteAsync(jpegData, cancellationToken);
				await _ffmpegInputStream.FlushAsync(cancellationToken);
			}
			catch (Exception ex)
			{
				// If ffmpeg has exited, include its exit code and recent stderr to help debug Broken pipe
				var extra = string.Empty;
				try
				{
					if (_ffmpegProcess != null && _ffmpegProcess.HasExited)
					{
						extra = $" (ffmpeg exited with code {_ffmpegProcess.ExitCode})";
					}
				}
				catch { }

				// collect recent stderr lines
				try
				{
					if (_ffmpegStderr.Count > 0)
					{
						extra += "\nRecent ffmpeg stderr:";
						foreach (var line in _ffmpegStderr)
						{
							extra += "\n  " + line;
						}
					}
				}
				catch { }

				Console.WriteLine($"Error sending frame to RTMP: {ex.Message}{extra}");
				throw;
			}
	}

	private string BuildFfmpegPipeArgs(string rtmpUrl)
	{
		// Read MJPEG frames from stdin, re-encode to H.264, output to RTMP
		// -f image2pipe tells ffmpeg to expect a stream of images
		// -c:v mjpeg tells it the input codec
		// -r sets output framerate
		var fpsArg = $"-r {_fps}";
		var bitrateArg = $"-b:v {_bitrateKbps}k -maxrate {_bitrateKbps}k -bufsize {_bitrateKbps * 2}k";
		// Use tune and profile for low-latency small streams
		return $"-f image2pipe -c:v mjpeg {fpsArg} -i pipe:0 " +
			   $"-c:v libx264 -preset veryfast -tune zerolatency -profile:v baseline -pix_fmt yuv420p -g {_fps * 2} " +
			   bitrateArg +
			   $" -an -f flv \"{rtmpUrl}\"";
	}

	public void Dispose()
	{
		try
		{
			_ffmpegInputStream?.Close();
			_ffmpegInputStream?.Dispose();
			
			if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
			{
				_ffmpegProcess.Kill(true);
				_ffmpegProcess.WaitForExit(5000);
			}
			
			_ffmpegProcess?.Dispose();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Error disposing RTMP connection: {ex.Message}");
		}
	}
}
