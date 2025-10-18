// Utility: Extract a single JPEG frame from MJPEG stream URL
static async Task<byte[]?> FetchSingleJpegFrameAsync(string mjpegUrl, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
{
	using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
	using var resp = await client.GetAsync(mjpegUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
	resp.EnsureSuccessStatusCode();
	using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
	var buffer = new byte[64 * 1024];
	using var ms = new MemoryStream();
	int bytesRead;
	// Read until we find a JPEG frame
	while (!cancellationToken.IsCancellationRequested)
	{
		bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
		if (bytesRead == 0) break;
		ms.Write(buffer, 0, bytesRead);
		if (MjpegReader.TryExtractJpeg(ms, out var jpegBytes) && jpegBytes != null)
		{
			return jpegBytes;
		}
	}
	return null;
}
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
string? key = config.GetValue<string>("YouTube:Key");
// Note: explicit support for a local service-account key file has been removed.
// Only `youtube_token.json` (user OAuth tokens) or configured OAuth credentials are supported.
string? oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
string? oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");


// Mode selection: support a string Mode ("read"/"serve"/"stream"/"poll")
var mode = config.GetValue<string>("Mode")?.ToLowerInvariant() ?? "serve"; // Default to serve mode
var readOnly = mode == "read";
var serve = mode == "serve";
var isPollingMode = mode == "poll";

// Determine if we should use OAuth-based broadcast creation or manual stream key
// We detect OAuth usage by presence of OAuth client credentials in config.
bool useOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);
// Service account support removed — only user OAuth via youtube_token.json is supported.

// Top-level cancellation token for graceful shutdown (propagated to streamers)
var appCts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
	Console.WriteLine("Stopping (Ctrl+C)...");
	try { appCts.Cancel(); } catch { }
	e.Cancel = true;
};

// Removed proactive token acquisition. Authentication now happens on-demand in the streaming path

if (readOnly)
{
	var reader = new MjpegReader(source!);
	await reader.StartAsync(CancellationToken.None);
	return;
}

if (mode == "testsrc")
{
	// Special diagnostic mode: create a YouTube broadcast+stream and push a test pattern (RTMPS) while polling ingestion.
	await StartTestPushAsync(config, appCts.Token);
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
	// In serve mode, don't auto-start YouTube streaming unless explicitly enabled in config
	var startYoutubeInServe = config.GetValue<bool?>("YouTube:StartInServe") ?? false;
	if ((useOAuth || !string.IsNullOrWhiteSpace(key)) && (!serve || startYoutubeInServe))
	{
		// Link the stream cancellation to the top-level app cancellation token
		streamCts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token);
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


if (isPollingMode)
{
	await PollAndStreamJobsAsync(config, appCts.Token);
	return;
}

// Default mode: stream to YouTube
await StartYouTubeStreamAsync(config, source!, key, appCts.Token);

static async Task PollAndStreamJobsAsync(IConfiguration config, CancellationToken cancellationToken)
{
	var moonrakerBase = config.GetValue<string>("Moonraker:BaseUrl") ?? "http://localhost:7125/";
	var apiKey = config.GetValue<string>("Moonraker:ApiKey");
	var authHeader = config.GetValue<string>("Moonraker:AuthHeader");
	var pollInterval = TimeSpan.FromSeconds(10); // configurable if desired
	string? lastJobFilename = null;
	CancellationTokenSource? streamCts = null;
	Task? streamTask = null;

	while (!cancellationToken.IsCancellationRequested)
	{
		try
		{
			// Query Moonraker job queue
			var baseUri = new Uri(moonrakerBase);
			var info = await MoonrakerClient.GetPrintInfoAsync(baseUri, apiKey, authHeader, cancellationToken);
			var currentJob = info?.Filename;

			if (!string.IsNullOrWhiteSpace(currentJob) && currentJob != lastJobFilename)
			{
				// New job detected, start stream
				Console.WriteLine($"[Watcher] New print job detected: {currentJob}");
				lastJobFilename = currentJob;
				if (streamCts != null)
				{
					try { streamCts.Cancel(); } catch { }
					if (streamTask != null) await streamTask;
				}
				streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				streamTask = Task.Run(async () =>
				{
					try
					{
						await StartYouTubeStreamAsync(config, config.GetValue<string>("Stream:Source")!, null, streamCts.Token);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[Watcher] Stream error: {ex.Message}");
					}
				}, streamCts.Token);
			}
			else if (string.IsNullOrWhiteSpace(currentJob) && lastJobFilename != null)
			{
				// Job finished, end stream
				Console.WriteLine($"[Watcher] Print job finished: {lastJobFilename}");
				lastJobFilename = null;
				if (streamCts != null)
				{
					try { streamCts.Cancel(); } catch { }
					if (streamTask != null) await streamTask;
					streamCts = null;
					streamTask = null;
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Watcher] Error: {ex.Message}");
		}

		await Task.Delay(pollInterval, cancellationToken);
	}

	// Cleanup on exit
	if (streamCts != null)
	{
		try { streamCts.Cancel(); } catch { }
		if (streamTask != null) await streamTask;
	}
}

static async Task StartYouTubeStreamAsync(IConfiguration config, string source, string? manualKey, CancellationToken cancellationToken)
{
	string? rtmpUrl = null;
	string? streamKey = null;
	string? broadcastId = null;
	YouTubeControlService? ytService = null;
	CancellationTokenSource? thumbnailCts = null;
	Task? thumbnailTask = null;

	try
	{
		var oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
		var oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");
		bool useOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);

		if (useOAuth)
		{
			Console.WriteLine("Using YouTube OAuth to create broadcast...");
			ytService = new YouTubeControlService(config);

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
			// Dump the LiveBroadcast and LiveStream resources for debugging
			try
			{
				await ytService.LogBroadcastAndStreamResourcesAsync(broadcastId, null, cancellationToken);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Failed to log broadcast/stream resources: {ex.Message}");
			}

			// Start thumbnail update task
			thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			thumbnailTask = Task.Run(async () =>
			{
				while (!thumbnailCts.Token.IsCancellationRequested)
				{
					try
					{
						var frame = await FetchSingleJpegFrameAsync(source, 10, thumbnailCts.Token);
						if (frame != null && !string.IsNullOrWhiteSpace(broadcastId))
						{
							var ok = await ytService.SetBroadcastThumbnailAsync(broadcastId, frame, thumbnailCts.Token);
							if (ok)
								Console.WriteLine($"Thumbnail updated at {DateTime.UtcNow:O}");
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Thumbnail update error: {ex.Message}");
					}
					await Task.Delay(TimeSpan.FromMinutes(1), thumbnailCts.Token);
				}
			}, thumbnailCts.Token);
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
		var targetFps = config.GetValue<int?>("Stream:TargetFps") ?? 6;
		var bitrateKbps = config.GetValue<int?>("Stream:BitrateKbps") ?? 800;

		if (useNativeStreamer)
		{
			Console.WriteLine($"Starting native .NET streamer to {rtmpUrl}/*** (fps={targetFps}, kbps={bitrateKbps})");
			streamer = new MjpegToRtmpStreamer(source, fullRtmpUrl, targetFps, bitrateKbps);
		}
		else
		{
			Console.WriteLine($"Starting ffmpeg streamer to {rtmpUrl}/*** (fps={targetFps}, kbps={bitrateKbps})");
			streamer = new FfmpegStreamer(source, fullRtmpUrl, targetFps, bitrateKbps);
		}
		
		// Start streamer without awaiting so we can detect ingestion while it's running
		var streamerStartTask = streamer.StartAsync(cancellationToken);

		// If we created a broadcast, transition it to live
			if (ytService != null && broadcastId != null)
		{
			Console.WriteLine("Stream started, waiting for YouTube ingestion to become active before transitioning to live...");
			// Wait up to 90s for ingestion to be detected by YouTube
			var ingestionOk = await ytService.WaitForIngestionAsync(null, TimeSpan.FromSeconds(90), cancellationToken);
			if (!ingestionOk)
			{
				Console.WriteLine("Warning: ingestion not active. Attempting transition anyway (may fail)...");
			}
			if (!cancellationToken.IsCancellationRequested)
			{
				await ytService.TransitionBroadcastToLiveWhenReadyAsync(broadcastId, TimeSpan.FromSeconds(90), 3, cancellationToken);
			}
			else
			{
				Console.WriteLine("Cancellation requested before transition; skipping TransitionBroadcastToLive.");
			}
		}

		// Wait for the stream to end (started earlier)
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
		// Stop thumbnail update task
		if (thumbnailCts != null)
		{
			try { thumbnailCts.Cancel(); } catch { }
			if (thumbnailTask != null) await thumbnailTask;
		}
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

static async Task StartTestPushAsync(IConfiguration config, CancellationToken cancellationToken)
{
	var oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
	var oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");
	if (string.IsNullOrWhiteSpace(oauthClientId) || string.IsNullOrWhiteSpace(oauthClientSecret))
	{
		Console.WriteLine("You must provide YouTube OAuth credentials in config to run testsrc mode.");
		return;
	}

	var yt = new YouTubeControlService(config);
	if (!await yt.AuthenticateAsync(cancellationToken))
	{
		Console.WriteLine("Failed to authenticate for testsrc.");
		return;
	}

	var res = await yt.CreateLiveBroadcastAsync(cancellationToken);
	if (res.rtmpUrl == null || res.streamKey == null)
	{
		Console.WriteLine("Failed to create broadcast for testsrc.");
		return;
	}

	var rtmpsUrl = res.rtmpUrl.Replace("rtmp://", "rtmps://") + "/" + res.streamKey;
	Console.WriteLine($"Pushing testsrc to: {rtmpsUrl}");

	// Start ffmpeg pushing testsrc
	var psi = new System.Diagnostics.ProcessStartInfo
	{
		FileName = "ffmpeg",
		Arguments = $"-re -f lavfi -i testsrc=size=640x480:rate=6 -c:v libx264 -preset veryfast -tune zerolatency -profile:v baseline -pix_fmt yuv420p -b:v 800k -maxrate 800k -bufsize 1600k -g 12 -f flv \"{rtmpsUrl}\"",
		UseShellExecute = false,
		RedirectStandardError = true,
		RedirectStandardOutput = true,
		CreateNoWindow = true
	};

	using var proc = System.Diagnostics.Process.Start(psi)!;
	if (proc == null)
	{
		Console.WriteLine("Failed to start ffmpeg for test push.");
		return;
	}

	// read stderr asynchronously to avoid blocking
	_ = Task.Run(async () =>
	{
		var sr = proc.StandardError;
		string? line;
		while ((line = await sr.ReadLineAsync()) != null)
		{
			if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine("[ffmpeg] " + line);
		}
	});

	// Poll ingestion while ffmpeg runs (timeout 45s)
	var pollTask = Task.Run(async () =>
	{
		var ok = await yt.WaitForIngestionAsync(null, TimeSpan.FromSeconds(45), cancellationToken);
		Console.WriteLine($"Ingestion active: {ok}");
		return ok;
	});

	// Wait for either proc exit or poll timeout
	var finished = await Task.WhenAny(proc.WaitForExitAsync(cancellationToken), pollTask);
	if (finished == pollTask)
	{
		Console.WriteLine("Poll completed. Killing ffmpeg push.");
		try { proc.Kill(true); } catch { }
	}
	else
	{
		Console.WriteLine("ffmpeg exited before poll completed.");
	}

	// Attempt to transition if ingestion active
	if (pollTask.IsCompleted && pollTask.Result)
	{
		Console.WriteLine("Attempting to transition broadcast to live after successful ingestion...");
		await yt.TransitionBroadcastToLiveWhenReadyAsync(res.broadcastId ?? string.Empty, TimeSpan.FromSeconds(30), 3, cancellationToken);
	}

	// cleanup
	Console.WriteLine("Ending test broadcast...");
	if (!string.IsNullOrEmpty(res.broadcastId))
	{
		await yt.EndBroadcastAsync(res.broadcastId, CancellationToken.None);
	}
	yt.Dispose();
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
	public static bool TryExtractJpeg(MemoryStream ms, out byte[]? jpegBytes)
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
