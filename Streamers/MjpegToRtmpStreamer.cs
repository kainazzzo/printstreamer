using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using PrintStreamer.Interfaces;

namespace PrintStreamer.Streamers
{
	/// <summary>
	/// Native .NET MJPEG to RTMP streamer that processes frames without external ffmpeg dependency.
	/// Reads MJPEG stream, extracts JPEG frames, and streams to RTMP destination.
	/// </summary>
	[Obsolete("EXPERIMENTAL/BROKEN: Native .NET MJPEG-to-RTMP streamer. Not production-ready. See NATIVE_STREAMER.md for details.")]
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

	public MjpegToRtmpStreamer(string sourceUrl, string rtmpUrl, int targetFps = 30, int bitrateKbps = 800, int maxQueue = 8)
	{
		_sourceUrl = sourceUrl;
		_rtmpUrl = rtmpUrl;
		_httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		_exitTcs = new TaskCompletionSource<object?>();
		_targetFps = targetFps <= 0 ? 30 : targetFps;
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
	[System.Obsolete("EXPERIMENTAL: MJPEG frame extraction for native streamer. Used only in experimental pipeline.")]
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
		// Strip APPn segments (EXIF/APP markers) which some cameras include and ffmpeg warns about
		try
		{
			frameData = StripJpegAppSegments(frame);
		}
		catch
		{
			// If stripping fails for any reason, fall back to raw frame
			frameData = frame;
		}

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

	/// <summary>
	/// Remove APP0..APP15 segments from a JPEG byte array. Returns a new byte[] without those segments.
	/// This helps avoid ffmpeg warnings for non-standard APP markers.
	/// </summary>
	private static ReadOnlyMemory<byte> StripJpegAppSegments(byte[] jpeg)
	{
		// Basic JPEG parsing: markers start with 0xFF followed by a marker byte.
		// APPn markers are 0xE0..0xEF and have a 2-byte length (big-endian) following the marker.
		int i = 0;
		using var ms = new MemoryStream();
		// copy SOI (0xFFD8) if present
		if (jpeg.Length >= 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
		{
			ms.WriteByte(0xFF);
			ms.WriteByte(0xD8);
			i = 2;
		}

		while (i + 1 < jpeg.Length)
		{
			if (jpeg[i] != 0xFF)
			{
				// copy remaining data and break
				ms.Write(jpeg, i, jpeg.Length - i);
				break;
			}
			byte marker = jpeg[i + 1];
			// EOI (0xD9) - copy and break
			if (marker == 0xD9)
			{
				ms.WriteByte(0xFF);
				ms.WriteByte(0xD9);
				break;
			}
			// Standalone markers (no length): 0x01 and 0xD0-0xD7
			if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
			{
				ms.WriteByte(0xFF);
				ms.WriteByte(marker);
				i += 2;
				continue;
			}

			// Otherwise marker has a 2-byte length
			if (i + 4 > jpeg.Length) break; // malformed
			int length = (jpeg[i + 2] << 8) | jpeg[i + 3];
			if (length < 2 || i + 2 + length > jpeg.Length) break; // malformed
			// APPn markers are 0xE0..0xEF
			if (marker >= 0xE0 && marker <= 0xEF)
			{
				// skip APPn segment
				i += 2 + length;
				continue;
			}
			// copy marker and its payload
			ms.WriteByte(0xFF);
			ms.WriteByte(marker);
			ms.Write(jpeg, i + 2, length);
			i += 2 + length;
		}

		return ms.ToArray();
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
	[System.Obsolete("EXPERIMENTAL: RTMP connection for native streamer. Used only in experimental pipeline.")]
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

	public RtmpConnection(string rtmpUrl, int fps, int bitrateKbps)
	{
		_rtmpUrl = rtmpUrl;
		_fps = fps;
		_bitrateKbps = bitrateKbps;
	}

	public async Task ConnectAsync(CancellationToken cancellationToken)
	{
		// Use ffmpeg as a simple pipe encoder for JPEG frames to RTMP/FLV.
		var gop = Math.Max(2, _fps * 2);
		var args = $"-hide_banner -loglevel error -f mjpeg -framerate {_fps} -i pipe:0 -c:v libx264 -pix_fmt yuv420p -preset veryfast -tune zerolatency -g {gop} -keyint_min {gop} -b:v {_bitrateKbps}k -maxrate {_bitrateKbps}k -bufsize {_bitrateKbps * 2}k -f flv {_rtmpUrl}";
		var psi = new System.Diagnostics.ProcessStartInfo
		{
			FileName = "ffmpeg",
			Arguments = args,
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};

		_ffmpegProcess = System.Diagnostics.Process.Start(psi);
		if (_ffmpegProcess == null)
		{
			throw new InvalidOperationException("Failed to start ffmpeg process for RTMP connection.");
		}
		_ffmpegInputStream = _ffmpegProcess.StandardInput.BaseStream;

		// Start reading stderr to capture logs
		_ = Task.Run(async () =>
		{
			try
			{
				using var reader = _ffmpegProcess.StandardError;
				string? line;
				while ((line = await reader.ReadLineAsync()) != null)
				{
					_ffmpegStderr.Enqueue(line);
					while (_ffmpegStderr.Count > _ffmpegStderrMax && _ffmpegStderr.TryDequeue(out _)) { }
				}
			}
			catch { }
		});

		await Task.Delay(250, cancellationToken); // brief startup
	}

	public async Task SendFrameAsync(ReadOnlyMemory<byte> jpegFrame, CancellationToken cancellationToken)
	{
		if (_ffmpegInputStream == null) throw new InvalidOperationException("RTMP connection not established.");
		await _ffmpegInputStream.WriteAsync(jpegFrame, cancellationToken);
		await _ffmpegInputStream.FlushAsync(cancellationToken);
	}

	public void Dispose()
	{
		try
		{
			_ffmpegInputStream?.Dispose();
			if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
			{
				_ffmpegProcess.Kill(true);
			}
			_ffmpegProcess?.Dispose();
		}
		catch { }
	}
	}
}
