using PrintStreamer.Interfaces;
using PrintStreamer.Streamers;
using PrintStreamer.Timelapse;
using PrintStreamer.Utils;
using PrintStreamer.Services;

// Moonraker polling and streaming helpers moved to Services/MoonrakerPoller.cs

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

// Register application services
webBuilder.Services.AddSingleton<TimelapseManager>();
webBuilder.Services.AddHostedService<PrintStreamer.Services.MoonrakerHostedService>();
// YouTubeControlService and other services are created on-demand inside poller/start methods

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
	Console.WriteLine("\nStopping (Ctrl+C)...");
	e.Cancel = true; // Prevent immediate termination
	try 
	{ 
		appCts.Cancel(); 
	} 
	catch (Exception ex) 
	{ 
		Console.WriteLine($"Error during cancellation: {ex.Message}"); 
	}
};

// Removed proactive token acquisition. Authentication now happens on-demand in the streaming path

if (readOnly)
{
	try
	{
		var reader = new MjpegReader(source!);
		await reader.StartAsync(appCts.Token);
	}
	catch (OperationCanceledException)
	{
		Console.WriteLine("Read mode cancelled.");
	}
	catch (Exception ex)
	{
		Console.WriteLine($"Read mode error: {ex.Message}");
	}
	return;
}

if (mode == "testsrc")
{
	// Special diagnostic mode: create a YouTube broadcast+stream and push a test pattern (RTMPS) while polling ingestion.
	try
	{
		await StartTestPushAsync(config, appCts.Token);
	}
	catch (OperationCanceledException)
	{
		Console.WriteLine("Test mode cancelled.");
	}
	catch (Exception ex)
	{
		Console.WriteLine($"Test mode error: {ex.Message}");
	}
	return;
}

// Ensure the WebApplication host is built and run in all modes so IHostedService instances start
// Configure Kestrel only when we're serving HTTP
if (serve)
{
	webBuilder.WebHost.ConfigureKestrel(options => { options.ListenAnyIP(8080); });
}

// Expose stream/task variables for shutdown handling
Task? streamTask = null;
CancellationTokenSource? streamCts = null;
TimelapseManager? timelapseManager = null;

var app = webBuilder.Build();

if (serve)
{
	// Start ASP.NET Core minimal server to proxy the MJPEG source to clients on /stream
	if (string.IsNullOrWhiteSpace(source))
	{
		Console.WriteLine("Error: --source is required when running in --serve mode.\n");
		PrintHelp();
		return;
	}

	var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    
	// Resolve timelapse manager from DI (registered earlier)
	timelapseManager = app.Services.GetRequiredService<TimelapseManager>();

	// If OAuth or stream key is provided, start YouTube streaming in the background
	var startYoutubeInServe = config.GetValue<bool?>("YouTube:StartInServe") ?? false;
	if ((useOAuth || !string.IsNullOrWhiteSpace(key)) && (!serve || startYoutubeInServe))
	{
		// Link the stream cancellation to the top-level app cancellation token
		streamCts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token);
		streamTask = Task.Run(async () =>
		{
			try
			{
				// StartYouTubeStreamAsync reads its configuration from IConfiguration directly
				await MoonrakerPoller.StartYouTubeStreamAsync(config, streamCts.Token, enableTimelapse: true, timelapseProvider: timelapseManager);
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

	// Timelapse API endpoints
	app.MapGet("/api/timelapses", (HttpContext ctx) =>
	{
		var timelapses = timelapseManager.GetAllTimelapses();
		return Results.Json(timelapses);
	});

	app.MapPost("/api/timelapses/{name}/start", async (string name, HttpContext ctx) =>
	{
		try
		{
			var sessionName = await timelapseManager.StartTimelapseAsync(name);
			if (sessionName != null)
			{
				return Results.Json(new { success = true, sessionName });
			}
			return Results.Json(new { success = false, error = "Failed to start timelapse" });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	app.MapPost("/api/timelapses/{name}/stop", async (string name, HttpContext ctx) =>
	{
		try
		{
			var videoPath = await timelapseManager.StopTimelapseAsync(name);
			return Results.Json(new { success = true, videoPath });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	app.MapGet("/api/timelapses/{name}/frames/{filename}", (string name, string filename, HttpContext ctx) =>
	{
		try
		{
			var timelapseDir = Path.Combine(timelapseManager.TimelapseDirectory, name);
			var filePath = Path.Combine(timelapseDir, filename);
            
			// Security check: ensure the file is within the timelapse directory
			if (!filePath.StartsWith(timelapseDir) || !File.Exists(filePath))
			{
				return Results.NotFound();
			}

			var contentType = filename.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : 
							 filename.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : 
							 "application/octet-stream";

			return Results.File(filePath, contentType);
		}
		catch
		{
			return Results.NotFound();
		}
	});

	// Enhanced test page with timelapse management
	app.MapGet("/", async (HttpContext ctx) =>
	{
			ctx.Response.ContentType = "text/html; charset=utf-8";
			var html = @"<!doctype html>
<html>
<head>
	<meta charset='utf-8'/>
	<title>PrintStreamer - Control Panel</title>
	<style>
		body{margin:0;font-family:sans-serif;background:#111;color:#eee} 
		.wrap{padding:20px;max-width:1200px;margin:0 auto} 
		.section{margin-bottom:30px;background:#222;padding:20px;border-radius:8px}
		.stream-container{text-align:center}
		img{display:block;max-width:100%;height:auto;border:4px solid #333;background:#000;margin:0 auto}
		button{background:#0066cc;color:white;border:none;padding:10px 20px;border-radius:4px;cursor:pointer;margin:5px}
		button:hover{background:#0052a3}
		button.danger{background:#cc0000}
		button.danger:hover{background:#a30000}
		button.success{background:#009900}
		button.success:hover{background:#007700}
		.timelapse-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:20px;margin-top:20px}
		.timelapse-item{background:#333;padding:15px;border-radius:8px}
		.timelapse-item.active{border:2px solid #0066cc}
		.status{padding:5px 10px;border-radius:4px;font-size:0.9em}
		.status.active{background:#009900}
		.status.inactive{background:#666}
		input{padding:8px;border-radius:4px;border:1px solid #666;background:#333;color:#eee;margin:5px}
		.video-link{color:#66ccff;text-decoration:none}
		.video-link:hover{text-decoration:underline}
	</style>
</head>
<body>
	<div class='wrap'>
		<h1>PrintStreamer - Control Panel</h1>
		
		<div class='section'>
			<h2>Live Stream</h2>
			<div class='stream-container'>
				<img id='mjpeg' src='/stream' alt='MJPEG stream' />
				<p>Direct stream URL: <a href='/stream' style='color:#66ccff'>/stream</a></p>
			</div>
		</div>

		<div class='section'>
			<h2>Timelapse Control</h2>
			<div style='margin-bottom:15px'>
				<input type='text' id='newTimelapseInput' placeholder='Timelapse name (e.g., print-job-name)' style='width:300px'>
				<button onclick='startTimelapse()' class='success'>Start New Timelapse</button>
				<button onclick='refreshTimelapses()'>Refresh</button>
			</div>
			<div id='timelapseList'></div>
		</div>
	</div>
	
	<script>
		// Basic reconnection: reload the img if it fails
		const img = document.getElementById('mjpeg');
		img.addEventListener('error', () => {
			console.log('Stream image error, retrying in 2s');
			setTimeout(() => { img.src = '/stream?ts=' + Date.now(); }, 2000);
		});

		// Timelapse management
		async function refreshTimelapses() {
			try {
				const response = await fetch('/api/timelapses');
				const timelapses = await response.json();
				displayTimelapses(timelapses);
			} catch (error) {
				console.error('Failed to load timelapses:', error);
			}
		}

		function displayTimelapses(timelapses) {
			const container = document.getElementById('timelapseList');
			if (timelapses.length === 0) {
				container.innerHTML = '<p>No timelapses found. Create one using the input above.</p>';
				return;
			}

			const html = timelapses.map(t => {
				let itemHtml = `<div class='timelapse-item ${t.isActive ? 'active' : ''}'>`;
				itemHtml += `<h3>${t.name}</h3>`;
				itemHtml += `<div class='status ${t.isActive ? 'active' : 'inactive'}'>${t.isActive ? 'RECORDING' : 'STOPPED'}</div>`;
				itemHtml += `<p><strong>Frames:</strong> ${t.frameCount}</p>`;
				itemHtml += `<p><strong>Started:</strong> ${new Date(t.startTime).toLocaleString()}</p>`;
				if (t.lastFrameTime) {
					itemHtml += `<p><strong>Last Frame:</strong> ${new Date(t.lastFrameTime).toLocaleString()}</p>`;
				}
				itemHtml += '<div>';
				if (t.isActive) {
					itemHtml += `<button onclick='stopTimelapse(""${t.name}"")' class='danger'>Stop Recording</button>`;
				} else {
					itemHtml += `<button onclick='startTimelapse(""${t.name}"")' class='success'>Resume</button>`;
				}
				for (const video of t.videoFiles) {
					itemHtml += ` <a href='/api/timelapses/${encodeURIComponent(t.name)}/frames/${encodeURIComponent(video)}' class='video-link' target='_blank'>📹 ${video}</a>`;
				}
				itemHtml += '</div></div>';
				return itemHtml;
			}).join('');
			
			container.innerHTML = `<div class='timelapse-grid'>${html}</div>`;
		}

		async function startTimelapse(name) {
			if (!name) {
				name = document.getElementById('newTimelapseInput').value.trim();
				if (!name) {
					alert('Please enter a timelapse name');
					return;
				}
			}

			try {
				const response = await fetch(`/api/timelapses/${encodeURIComponent(name)}/start`, { method: 'POST' });
				const result = await response.json();
				if (result.success) {
					document.getElementById('newTimelapseInput').value = '';
					await refreshTimelapses();
				} else {
					alert('Failed to start timelapse: ' + (result.error || 'Unknown error'));
				}
			} catch (error) {
				alert('Error starting timelapse: ' + error.message);
			}
		}

		async function stopTimelapse(name) {
			if (!confirm(`Stop recording '${name}' and create video?`)) return;

			try {
				const response = await fetch(`/api/timelapses/${encodeURIComponent(name)}/stop`, { method: 'POST' });
				const result = await response.json();
				if (result.success) {
					await refreshTimelapses();
					if (result.videoPath) {
						alert('Video created successfully!');
					}
				} else {
					alert('Failed to stop timelapse: ' + (result.error || 'Unknown error'));
				}
			} catch (error) {
				alert('Error stopping timelapse: ' + error.message);
			}
		}

		// Load timelapses on page load
		refreshTimelapses();
		
		// Auto-refresh every 30 seconds
		setInterval(refreshTimelapses, 30000);
	</script>
</body>
</html>";

				await ctx.Response.WriteAsync(html);
		});

    Console.WriteLine("Starting proxy server on http://0.0.0.0:8080/stream");

    // Handle graceful shutdown - this will run regardless of mode since the host is started below
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        Console.WriteLine("Shutting down...");
        streamCts?.Cancel();
        timelapseManager?.Dispose();
    });

}

// Start the host so IHostedService instances (MoonrakerHostedService) are started in all modes
await app.RunAsync();

// If we were in serve mode, wait for the background stream task (if any) to complete and clean up
if (serve)
{
    if (streamTask != null)
    {
        await streamTask;
    }
    timelapseManager?.Dispose();
}
// Poll/stream behavior is handled by MoonrakerHostedService when configured

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