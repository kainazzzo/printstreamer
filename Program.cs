// Simple CLI to either read an MJPEG stream (OctoPrint/webcam) directly in C# or stream a video source to YouTube using ffmpeg.
// Usage:
//   Read-only (inspect MJPEG frames):
//     dotnet run -- --read --source <mjpeg-url>
//   Stream to YouTube (ffmpeg):
//     dotnet run -- --source <input> --key <youtube-stream-key>
// Example read-only (your OctoPrint stream):
//   dotnet run -- --read --source "http://192.168.1.117/webcam/?action=stream&octoAppPortOverride=80&cacheBust=1759967901624"
//
// TODO: add YouTube programmatic management (create/manage liveBroadcast resources) using
// the Google.Apis.YouTube.v3 NuGet package. For ffmpeg orchestration consider Xabe.FFmpeg or CliWrap.

// Use the default WebApplication builder so standard configuration (appsettings.json, env, args) is loaded
var webBuilder = WebApplication.CreateBuilder(args);
var config = webBuilder.Configuration;

// If user passed -h/--help, show help and exit (preserve manual help flag behavior)
if (Array.IndexOf(args, "-h") >= 0 || Array.IndexOf(args, "--help") >= 0)
{
	PrintHelp();
	return;
}

// Read configuration values (appsettings.json, environment, command-line)
string? source = config.GetValue<string>("Stream:Source");
string? key = config.GetValue<string>("YouTube:Key") ?? Environment.GetEnvironmentVariable("YOUTUBE_STREAM_KEY");

// Mode selection: support a string Mode ("read"/"serve"/"stream") or boolean flags Read/Serve
var mode = config.GetValue<string>("Mode")?.ToLowerInvariant();
var readOnly = false;
var serve = false;
if (!string.IsNullOrEmpty(mode))
{
	if (mode == "read") readOnly = true;
	else if (mode == "serve") serve = true;
}
else
{
	readOnly = config.GetValue<bool>("Read");
	serve = config.GetValue<bool>("Serve");
}

if (string.IsNullOrWhiteSpace(source))
{
	Console.WriteLine("Error: Stream:Source is required (set in appsettings.json, environment, or command-line).\n");
	PrintHelp();
	return;
}

if (!readOnly && !serve && string.IsNullOrWhiteSpace(key))
{
	Console.WriteLine("Error: --key is required when not running in --read or --serve mode.\n");
	PrintHelp();
	return;
}

Console.CancelKeyPress += (s, e) =>
{
	Console.WriteLine("Stopping (Ctrl+C)...");
	// allow process to exit; individual components handle cancellation/stop
	e.Cancel = true;
};

if (readOnly)
{
	var reader = new MjpegReader(source!);
	await reader.StartAsync(CancellationToken.None);
	return;
}

if (serve)
{
	// Start ASP.NET Core minimal server to proxy the MJPEG source to clients on /stream
	if (string.IsNullOrWhiteSpace(source))
	{
		Console.WriteLine("Error: --source is required when running in --serve mode.\n");
		PrintHelp();
		return;
	}

	webBuilder.WebHost.ConfigureKestrel(options => { options.ListenAnyIP(8080); });
	var app = webBuilder.Build();

	var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

	app.MapGet("/stream", async (HttpContext ctx) =>
	{
		Console.WriteLine($"Client connected: {ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort}");

		try
		{
			using var req = new HttpRequestMessage(HttpMethod.Get, source);
			using var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
			resp.EnsureSuccessStatusCode();

			// Propagate content-type (e.g., multipart/x-mixed-replace;boundary=...)
			ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
			ctx.Response.StatusCode = 200;

			using var upstream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
			await upstream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
		}
		catch (OperationCanceledException)
		{
			// client disconnected or cancellation
			Console.WriteLine("Client disconnected or request canceled.");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Error proxying stream: {ex.Message}");
			if (!ctx.Response.HasStarted)
			{
				ctx.Response.StatusCode = 502;
			}
		}
	});

		// Simple test page to view the MJPEG stream in a browser
		app.MapGet("/", async (HttpContext ctx) =>
		{
				ctx.Response.ContentType = "text/html; charset=utf-8";
				var html = @"<!doctype html>
<html>
<head>
	<meta charset='utf-8'/>
	<title>PrintStreamer - MJPEG Test</title>
	<style>body{margin:0;font-family:sans-serif;background:#111;color:#eee} .wrap{padding:10px;} img{display:block;max-width:100%;height:auto;border:4px solid #222;background:#000}</style>
</head>
<body>
	<div class='wrap'>
		<h1>PrintStreamer - MJPEG Test</h1>
		<p>If the stream is available, the image below will update with frames from <code>/stream</code>.</p>
		<img id='mjpeg' src='/stream' alt='MJPEG stream' />
		<p>Direct stream URL: <a href='/stream'>/stream</a></p>
	</div>
	<script>
		// Basic reconnection: reload the img if it fails
		const img = document.getElementById('mjpeg');
		img.addEventListener('error', () => {
			console.log('Stream image error, retrying in 2s');
			setTimeout(() => { img.src = '/stream?ts=' + Date.now(); }, 2000);
		});
	</script>
</body>
</html>";

				await ctx.Response.WriteAsync(html);
		});

	Console.WriteLine("Starting proxy server on http://0.0.0.0:8080/stream");
	await app.RunAsync();
	return;
}

var streamer = new FfmpegStreamer(source!, key!);
await streamer.StartAsync();

static void PrintHelp()
{
	Console.WriteLine("printstreamer - simple ffmpeg-based streamer / MJPEG inspector");
	Console.WriteLine();
	Console.WriteLine("Options:");
	Console.WriteLine("  -s, --source <input>   Video input. HTTP MJPEG URL (http://...) or local device (/dev/video0) or file path.");
	Console.WriteLine("  -k, --key <streamkey>  YouTube stream key (the part after rtmp URL).");
	Console.WriteLine("  -r, --read             Read MJPEG stream locally and print frame info (no YouTube / ffmpeg use).\n");
	Console.WriteLine();
	Console.WriteLine("Example:");
	Console.WriteLine("  dotnet run -- --read --source \"http://192.168.1.117/webcam/?action=stream&octoAppPortOverride=80&cacheBust=1759967901624\"\n");
}

internal class MjpegReader
{
	private readonly string _url;

	public MjpegReader(string url)
	{
		_url = url;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

		Console.WriteLine($"Connecting to MJPEG stream: {_url}");

		using var resp = await client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		resp.EnsureSuccessStatusCode();

		using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);

		Console.WriteLine("Connected. Reading frames...");

		var buffer = new byte[64 * 1024];
		using var ms = new MemoryStream();
		int bytesRead;
		long totalRead = 0;
		int frameCount = 0;

		// Simple MJPEG frame extraction by searching for JPEG SOI/EOI markers
		while (!cancellationToken.IsCancellationRequested)
		{
			bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
			if (bytesRead == 0) break;
			ms.Write(buffer, 0, bytesRead);
			totalRead += bytesRead;

			// Try to extract frames from ms
			while (TryExtractJpeg(ms, out var jpegBytes))
			{
				if (jpegBytes == null) continue;
				frameCount++;
				Console.WriteLine($"Frame {frameCount}: {jpegBytes.Length} bytes at {DateTime.UtcNow:O}");

				// Save every 30th frame as an example
				if (frameCount % 30 == 0)
				{
					var dir = Path.Combine(Directory.GetCurrentDirectory(), "frames");
					Directory.CreateDirectory(dir);
					var path = Path.Combine(dir, $"frame_{frameCount}.jpg");
					await File.WriteAllBytesAsync(path, jpegBytes, cancellationToken);
					Console.WriteLine($"Saved {path}");
				}
			}
		}

		Console.WriteLine($"Stream ended, total frames: {frameCount}, total bytes read: {totalRead}");
	}

	// Looks for the first JPEG (0xFFD8 ... 0xFFD9) in the memory stream.
	// If found, returns true and sets jpegBytes (and removes data up to end of JPEG from the stream).
	private static bool TryExtractJpeg(MemoryStream ms, out byte[]? jpegBytes)
	{
		jpegBytes = null;
		var buf = ms.ToArray();
		var len = buf.Length;
		int soi = -1;
		for (int i = 0; i < len - 1; i++)
		{
			if (buf[i] == 0xFF && buf[i + 1] == 0xD8)
			{
				soi = i;
				break;
			}
		}
		if (soi == -1) return false;

		int eoi = -1;
		for (int i = soi + 2; i < len - 1; i++)
		{
			if (buf[i] == 0xFF && buf[i + 1] == 0xD9)
			{
				eoi = i + 1; // index of second byte of marker
				break;
			}
		}
		if (eoi == -1) return false;

		var frameLen = eoi - soi + 1;
		jpegBytes = new byte[frameLen];
		Array.Copy(buf, soi, jpegBytes, 0, frameLen);

		// Remove consumed bytes from the MemoryStream by shifting remaining data to a fresh MemoryStream
		var remaining = len - (eoi + 1);
		ms.SetLength(0);
		if (remaining > 0)
		{
			ms.Write(buf, eoi + 1, remaining);
		}
		ms.Position = 0;
		return true;
	}
}
