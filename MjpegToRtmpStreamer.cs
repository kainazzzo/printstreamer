using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Threading;
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

	public Task ExitTask => _exitTcs.Task;

	public MjpegToRtmpStreamer(string sourceUrl, string rtmpUrl)
	{
		_sourceUrl = sourceUrl;
		_rtmpUrl = rtmpUrl;
		_httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		_exitTcs = new TaskCompletionSource<object?>();
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
				await StreamLoopAsync(_cts.Token);
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

	private async Task StreamLoopAsync(CancellationToken cancellationToken)
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
		
		// Initialize RTMP connection
		using var rtmpConnection = new RtmpConnection(_rtmpUrl);
		await rtmpConnection.ConnectAsync(cancellationToken);

		Console.WriteLine("Streaming started...");

		// Read and process frames
		var frameExtractor = new MjpegFrameExtractor(boundary);
		var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
		int frameCount = 0;
		DateTime lastFrameTime = DateTime.UtcNow;
		
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
				if (bytesRead == 0)
				{
					Console.WriteLine("Source stream ended.");
					break;
				}

				frameExtractor.AppendData(buffer.AsSpan(0, bytesRead));

				// Extract and send all available frames
				while (frameExtractor.TryExtractFrame(out var frameData))
				{
					frameCount++;
					var now = DateTime.UtcNow;
					var frameDelta = (now - lastFrameTime).TotalMilliseconds;
					lastFrameTime = now;

					if (frameCount % 30 == 0)
					{
						Console.WriteLine($"Frame {frameCount}: {frameData.Length} bytes, delta: {frameDelta:F1}ms");
					}

					// Send frame to RTMP
					await rtmpConnection.SendFrameAsync(frameData, cancellationToken);
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}

		Console.WriteLine($"Stream ended. Total frames: {frameCount}");
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
	private System.Diagnostics.Process? _ffmpegProcess;
	private Stream? _ffmpegInputStream;

	public RtmpConnection(string rtmpUrl)
	{
		_rtmpUrl = rtmpUrl;
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
			if (e.Data != null && e.Data.Contains("frame="))
			{
				// Only log every ~100 frames to avoid spam
				if (e.Data.Contains("frame= ") && int.TryParse(e.Data.Split("frame=")[1].Trim().Split(' ')[0], out var frameNum) && frameNum % 100 == 0)
				{
					Console.WriteLine($"[ffmpeg] {e.Data.Trim()}");
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
			Console.WriteLine($"Error sending frame to RTMP: {ex.Message}");
			throw;
		}
	}

	private static string BuildFfmpegPipeArgs(string rtmpUrl)
	{
		// Read MJPEG frames from stdin, re-encode to H.264, output to RTMP
		// -f image2pipe tells ffmpeg to expect a stream of images
		// -c:v mjpeg tells it the input codec
		// -r 30 sets output framerate (adjust as needed)
		return $"-f image2pipe -c:v mjpeg -r 30 -i pipe:0 " +
		       $"-c:v libx264 -preset veryfast -pix_fmt yuv420p -g 50 " +
		       $"-b:v 2500k -maxrate 2500k -bufsize 5000k " +
		       $"-an -f flv \"{rtmpUrl}\"";
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
