// PrintStreamer - Stream 3D printer webcam to YouTube Live
// Configuration is loaded from appsettings.json, environment variables, and command-line arguments.
//
// Usage examples:
//   dotnet run -- --Mode serve
//   dotnet run -- --Mode stream --Stream:Source "http://printer.local/webcam/?action=stream"
//   dotnet run -- --Mode read --Stream:Source "http://printer.local/webcam/?action=stream"
//
// Environment variables:
//   export Stream__Source="http://printer.local/webcam/?action=stream"
//   export Mode=serve
//   dotnet run
//
// See README.md for full documentation.

// Check for help request before loading full configuration
if (args.Any(a => a == "--help" || a == "-h" || a == "/?" || a.Equals("help", StringComparison.OrdinalIgnoreCase)))
{
	PrintHelp();
	return;
}

// Use the default WebApplication builder so standard configuration (appsettings.json, env, args) is loaded
var webBuilder = WebApplication.CreateBuilder(args);
var config = webBuilder.Configuration;

// Read configuration values
string? source = config.GetValue<string>("Stream:Source");
string? key = config.GetValue<string>("YouTube:Key") ?? Environment.GetEnvironmentVariable("YOUTUBE_STREAM_KEY");
string? oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
string? oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");

// Mode selection: support a string Mode ("read"/"serve"/"stream")
var mode = config.GetValue<string>("Mode")?.ToLowerInvariant() ?? "serve"; // Default to serve mode
var readOnly = mode == "read";
var serve = mode == "serve";

// Determine if we should use OAuth-based broadcast creation or manual stream key
bool useOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);

if (string.IsNullOrWhiteSpace(source))
{
	Console.WriteLine("Error: Stream:Source is required.");
	Console.WriteLine();
	PrintHelp();
	return;
}

if (!readOnly && !serve && !useOAuth && string.IsNullOrWhiteSpace(key))
{
	Console.WriteLine("Error: Either YouTube:Key or YouTube:OAuth credentials are required for streaming.");
	Console.WriteLine();
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

	// If OAuth or stream key is provided, start YouTube streaming in the background
	Task? streamTask = null;
	CancellationTokenSource? streamCts = null;
	if (useOAuth || !string.IsNullOrWhiteSpace(key))
	{
		streamCts = new CancellationTokenSource();
		streamTask = Task.Run(async () =>
		{
			try
			{
				await StartYouTubeStreamAsync(config, source!, key, streamCts.Token);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"YouTube streaming error: {ex.Message}");
			}
		}, streamCts.Token);
	}

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
	
	// Handle graceful shutdown
	var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
	lifetime.ApplicationStopping.Register(() =>
	{
		Console.WriteLine("Shutting down...");
		streamCts?.Cancel();
	});

	await app.RunAsync();
	
	// Wait for streaming task to complete
	if (streamTask != null)
	{
		await streamTask;
	}
	
	return;
}

// Default mode: stream to YouTube
await StartYouTubeStreamAsync(config, source!, key, CancellationToken.None);

static async Task StartYouTubeStreamAsync(IConfiguration config, string source, string? manualKey, CancellationToken cancellationToken)
{
	string? rtmpUrl = null;
	string? streamKey = null;
	string? broadcastId = null;
	YouTubeBroadcastService? ytService = null;

	try
	{
		var oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
		var oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");
		bool useOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);

		if (useOAuth)
		{
			Console.WriteLine("Using YouTube OAuth to create broadcast...");
			ytService = new YouTubeBroadcastService(config);

			// Authenticate
			if (!await ytService.AuthenticateAsync(cancellationToken))
			{
				Console.WriteLine("Failed to authenticate with YouTube. Exiting.");
				return;
			}

			// Create broadcast and stream
			var result = await ytService.CreateLiveBroadcastAsync(cancellationToken);
			if (result.rtmpUrl == null || result.streamKey == null)
			{
				Console.WriteLine("Failed to create YouTube broadcast. Exiting.");
				return;
			}

			rtmpUrl = result.rtmpUrl;
			streamKey = result.streamKey;
			broadcastId = result.broadcastId;

			Console.WriteLine($"YouTube broadcast created! Watch at: https://www.youtube.com/watch?v={broadcastId}");
		}
		else if (!string.IsNullOrWhiteSpace(manualKey))
		{
			Console.WriteLine("Using manual YouTube stream key...");
			rtmpUrl = "rtmp://a.rtmp.youtube.com/live2";
			streamKey = manualKey;
		}
		else
		{
			Console.WriteLine("Error: No YouTube credentials or stream key provided.");
			return;
		}

		// Start streaming with chosen implementation
		var fullRtmpUrl = $"{rtmpUrl}/{streamKey}";
		var useNativeStreamer = config.GetValue<bool>("Stream:UseNativeStreamer");
		
		IStreamer streamer;
		if (useNativeStreamer)
		{
			Console.WriteLine($"Starting native .NET streamer to {rtmpUrl}/***");
			streamer = new MjpegToRtmpStreamer(source, fullRtmpUrl);
		}
		else
		{
			Console.WriteLine($"Starting ffmpeg streamer to {rtmpUrl}/***");
			streamer = new FfmpegStreamer(source, fullRtmpUrl);
		}
		
		await streamer.StartAsync(cancellationToken);

		// If we created a broadcast, transition it to live
		if (ytService != null && broadcastId != null)
		{
			Console.WriteLine("Stream started, waiting a few seconds before transitioning to live...");
			await Task.Delay(10000, cancellationToken); // Give ffmpeg time to connect
			await ytService.TransitionBroadcastToLiveAsync(broadcastId, cancellationToken);
		}

		// Wait for the stream to end
		await streamer.ExitTask;
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
		// Clean up YouTube broadcast if created
		if (ytService != null && broadcastId != null)
		{
			Console.WriteLine("Ending YouTube broadcast...");
			try
			{
				await ytService.EndBroadcastAsync(broadcastId, CancellationToken.None);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Failed to end broadcast: {ex.Message}");
			}
		}

		ytService?.Dispose();
	}
}

static void PrintHelp()
{
	Console.WriteLine("PrintStreamer - Stream 3D printer webcam to YouTube Live");
	Console.WriteLine();
	Console.WriteLine("Configuration Methods:");
	Console.WriteLine("  1. appsettings.json (base configuration)");
	Console.WriteLine("  2. Environment variables (use __ for nested keys, e.g., Stream__Source)");
	Console.WriteLine("  3. Command-line arguments (use --Key value or --Key=value)");
	Console.WriteLine();
	Console.WriteLine("Configuration Keys:");
	Console.WriteLine("  Mode                              - serve (default), stream, or read");
	Console.WriteLine("  Stream:Source                     - MJPEG URL (required)");
	Console.WriteLine("  Stream:UseNativeStreamer          - true/false (default: false)");
	Console.WriteLine("  YouTube:Key                       - Manual stream key (optional)");
	Console.WriteLine("  YouTube:OAuth:ClientId            - OAuth client ID (optional)");
	Console.WriteLine("  YouTube:OAuth:ClientSecret        - OAuth client secret (optional)");
	Console.WriteLine();
	Console.WriteLine("Modes:");
	Console.WriteLine("  serve   - MJPEG proxy server on :8080 + YouTube streaming (if configured)");
	Console.WriteLine("  stream  - Direct YouTube streaming only");
	Console.WriteLine("  read    - Diagnostic mode (prints frame info, no streaming)");
	Console.WriteLine();
	Console.WriteLine("Examples:");
	Console.WriteLine();
	Console.WriteLine("  # Run with defaults from appsettings.json");
	Console.WriteLine("  dotnet run");
	Console.WriteLine();
	Console.WriteLine("  # Override mode via command-line");
	Console.WriteLine("  dotnet run -- --Mode stream");
	Console.WriteLine();
	Console.WriteLine("  # Set multiple options on command-line");
	Console.WriteLine("  dotnet run -- --Mode stream --Stream:Source \"http://printer/webcam/?action=stream\"");
	Console.WriteLine();
	Console.WriteLine("  # Use environment variables");
	Console.WriteLine("  export Stream__Source=\"http://printer/webcam/?action=stream\"");
	Console.WriteLine("  export Mode=stream");
	Console.WriteLine("  export Stream__UseNativeStreamer=true");
	Console.WriteLine("  dotnet run");
	Console.WriteLine();
	Console.WriteLine("  # Mix config file, env vars, and command-line");
	Console.WriteLine("  # (Command-line > Env vars > appsettings.json)");
	Console.WriteLine("  dotnet run -- --Mode serve --YouTube:Key \"your-key\"");
	Console.WriteLine();
	Console.WriteLine("See README.md for complete documentation.");
	Console.WriteLine();
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
