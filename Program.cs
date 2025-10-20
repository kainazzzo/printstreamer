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

// Utility: Sanitize filename for use as folder name
static string SanitizeFilename(string filename)
{
	if (string.IsNullOrWhiteSpace(filename))
		return "unknown";
	
	// Remove file extension
	var nameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
	
	// Define characters to remove or replace
	var invalidChars = Path.GetInvalidFileNameChars()
		.Concat(Path.GetInvalidPathChars())
		.Concat(new[] { ' ', '-', '(', ')', '[', ']', '{', '}', ':', ';', ',', '.', '#' })
		.Distinct()
		.ToArray();
	
	var result = nameWithoutExtension;
	
	// Replace invalid characters
	foreach (var c in invalidChars)
	{
		result = result.Replace(c, '_');
	}
	
	// Special replacements
	result = result.Replace("&", "and");
	
	// Clean up multiple underscores
	while (result.Contains("__"))
	{
		result = result.Replace("__", "_");
	}
	
	// Trim underscores from start and end
	result = result.Trim('_');
	
	// Ensure we have a valid result
	return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
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
	
	// Initialize timelapse manager
	var timelapseManager = new TimelapseManager(config);

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
					await StartYouTubeStreamAsync(config, source!, key, streamCts.Token, enableTimelapse: true, timelapseProvider: timelapseManager);
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
	
	// Handle graceful shutdown
	var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
	lifetime.ApplicationStopping.Register(() =>
	{
		Console.WriteLine("Shutting down...");
		streamCts?.Cancel();
		timelapseManager?.Dispose();
	});

	await app.RunAsync();
	
	// Wait for streaming task to complete
	if (streamTask != null)
	{
		await streamTask;
	}
	
	// Final cleanup
	timelapseManager?.Dispose();
	
	return;
}


if (isPollingMode)
{
	try
	{
		await PollAndStreamJobsAsync(config, appCts.Token);
	}
	catch (OperationCanceledException)
	{
		Console.WriteLine("Polling mode cancelled.");
	}
	catch (Exception ex)
	{
		Console.WriteLine($"Polling mode error: {ex.Message}");
	}
	return;
}

// Default mode: stream to YouTube
try
{
	await StartYouTubeStreamAsync(config, source!, key, appCts.Token, enableTimelapse: true, timelapseProvider: null);
}
catch (OperationCanceledException)
{
	Console.WriteLine("Streaming cancelled.");
}
catch (Exception ex)
{
	Console.WriteLine($"Streaming error: {ex.Message}");
}

static async Task PollAndStreamJobsAsync(IConfiguration config, CancellationToken cancellationToken)
{
	var moonrakerBase = config.GetValue<string>("Moonraker:BaseUrl") ?? "http://localhost:7125/";
	var apiKey = config.GetValue<string>("Moonraker:ApiKey");
	var authHeader = config.GetValue<string>("Moonraker:AuthHeader");
	var basePollInterval = TimeSpan.FromSeconds(10); // configurable if desired
	var fastPollInterval = TimeSpan.FromSeconds(2); // faster polling near completion
	string? lastJobFilename = null;
	string? lastCompletedJobFilename = null; // used for final upload/title if app shuts down post-completion
	CancellationTokenSource? streamCts = null;
	Task? streamTask = null;
	TimelapseService? timelapse = null;
	CancellationTokenSource? timelapseCts = null;
	Task? timelapseTask = null;
	YouTubeControlService? ytService = null;
	TimelapseManager? timelapseManager = null;
	string? activeTimelapseSessionName = null; // track current timelapse session in manager
	// New state to support last-layer early finalize
	bool lastLayerTriggered = false;
	Task? timelapseFinalizeTask = null;

	try
	{
		// Initialize TimelapseManager for G-code caching and frame capture
		timelapseManager = new TimelapseManager(config);
		
		// Initialize YouTube service if credentials are provided (for timelapse upload)
		var oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
		var oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");
		bool useOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);
		if (useOAuth)
		{
			ytService = new YouTubeControlService(config);
			var authOk = await ytService.AuthenticateAsync(cancellationToken);
			if (!authOk)
			{
				Console.WriteLine("[Watcher] YouTube authentication failed. Timelapse upload will be disabled.");
				ytService.Dispose();
				ytService = null;
			}
			else
			{
				Console.WriteLine("[Watcher] YouTube authenticated successfully for timelapse uploads.");
			}
		}

		while (!cancellationToken.IsCancellationRequested)
		{
			TimeSpan pollInterval = basePollInterval; // Default poll interval
			try
			{
			// Query Moonraker job queue
			var baseUri = new Uri(moonrakerBase);
			var info = await MoonrakerClient.GetPrintInfoAsync(baseUri, apiKey, authHeader, cancellationToken);
			var currentJob = info?.Filename;
			var jobQueueId = info?.JobQueueId;
			var state = info?.State;
			var isPrinting = string.Equals(state, "printing", StringComparison.OrdinalIgnoreCase);
			var remaining = info?.Remaining;
			var progressPct = info?.ProgressPercent;
			var currentLayer = info?.CurrentLayer;
			var totalLayers = info?.TotalLayers;

			Console.WriteLine($"[Watcher] Poll result - Filename: '{currentJob}', State: '{state}', Progress: {progressPct?.ToString("F1") ?? "n/a"}%, Remaining: {remaining?.ToString() ?? "n/a"}, Layer: {currentLayer?.ToString() ?? "n/a"}/{totalLayers?.ToString() ?? "n/a"}");

			// Track if a stream is already active
			var streamingActive = streamCts != null && streamTask != null && !streamTask.IsCompleted;

			// Start stream when actively printing (even if filename is missing initially)
			if (isPrinting && !streamingActive && (string.IsNullOrWhiteSpace(currentJob) || currentJob != lastJobFilename))
			{
				// New job detected, start stream and timelapse
				Console.WriteLine($"[Watcher] New print job detected: {currentJob ?? "(unknown)"}");
				lastJobFilename = currentJob ?? $"__printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
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
						// In polling mode, disable internal timelapse in streaming path to avoid duplicate uploads
						await StartYouTubeStreamAsync(config, config.GetValue<string>("Stream:Source")!, null, streamCts.Token, enableTimelapse: false, timelapseProvider: null);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[Watcher] Stream error: {ex.Message}");
					}
				}, streamCts.Token);
				// Start timelapse using TimelapseManager (will download G-code and cache metadata)
				{
					var jobNameSafe = !string.IsNullOrWhiteSpace(currentJob) ? SanitizeFilename(currentJob) : $"printing_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
					Console.WriteLine($"[Watcher] Starting timelapse session: {jobNameSafe}");
					Console.WriteLine($"[Watcher]   - currentJob: '{currentJob}'");
					Console.WriteLine($"[Watcher]   - jobNameSafe: '{jobNameSafe}'");
					
					// Start timelapse via manager (downloads G-code, caches metadata, captures initial frame)
					activeTimelapseSessionName = await timelapseManager!.StartTimelapseAsync(jobNameSafe, currentJob);
					if (activeTimelapseSessionName != null)
					{
						Console.WriteLine($"[Watcher] Timelapse session started: {activeTimelapseSessionName}");
					}
					else
					{
						Console.WriteLine($"[Watcher] Warning: Failed to start timelapse session");
					}
					
					lastLayerTriggered = false; // reset for new job
					timelapseFinalizeTask = null;
					
					// Note: TimelapseManager handles periodic frame capture internally via its timer
					// No need for manual timelapseTask in poll mode
				}
			}
			// Detect last-layer and finalize timelapse early (while keeping live stream running)
			else if (isPrinting && activeTimelapseSessionName != null && !lastLayerTriggered)
			{
				// More aggressive defaults to catch the last layer of actual printing (not cooldown/retraction)
				var thresholdSecs = config.GetValue<int?>("Timelapse:LastLayerRemainingSeconds") ?? 30;
				var thresholdPct = config.GetValue<double?>("Timelapse:LastLayerProgressPercent") ?? 98.5;
				var layerThreshold = config.GetValue<int?>("Timelapse:LastLayerOffset") ?? 1; // trigger one layer earlier (avoid one extra frame)
				
				bool lastLayerByTime = remaining.HasValue && remaining.Value <= TimeSpan.FromSeconds(thresholdSecs);
				bool lastLayerByProgress = progressPct.HasValue && progressPct.Value >= thresholdPct;
				bool lastLayerByLayer = currentLayer.HasValue && totalLayers.HasValue && 
				                        totalLayers.Value > 0 && 
				                        currentLayer.Value >= (totalLayers.Value - layerThreshold);
				
				if (lastLayerByTime || lastLayerByProgress || lastLayerByLayer)
				{
					Console.WriteLine($"[Timelapse] *** Last-layer detected ***");
					Console.WriteLine($"[Timelapse]   Remaining time: {remaining?.ToString() ?? "n/a"} (threshold: {thresholdSecs}s, triggered: {lastLayerByTime})");
					Console.WriteLine($"[Timelapse]   Progress: {progressPct?.ToString("F1") ?? "n/a"}% (threshold: {thresholdPct}%, triggered: {lastLayerByProgress})");
					Console.WriteLine($"[Timelapse]   Layer: {currentLayer?.ToString() ?? "n/a"}/{totalLayers?.ToString() ?? "n/a"} (threshold: -{layerThreshold}, triggered: {lastLayerByLayer})");
					Console.WriteLine($"[Timelapse] Capturing final frame and finalizing timelapse now...");
					lastLayerTriggered = true;

					// Stop timelapse via manager and kick off finalize/upload in the background
					var sessionToFinalize = activeTimelapseSessionName;
					var uploadEnabled = config.GetValue<bool?>("Timelapse:Upload") ?? false;
					timelapseFinalizeTask = Task.Run(async () =>
					{
						try
						{
							Console.WriteLine($"[Timelapse] Stopping timelapse session (early finalize): {sessionToFinalize}");
							var createdVideoPath = await timelapseManager!.StopTimelapseAsync(sessionToFinalize!);

							if (!string.IsNullOrWhiteSpace(createdVideoPath) && File.Exists(createdVideoPath) && uploadEnabled && ytService != null)
							{
								try
								{
									Console.WriteLine("[Timelapse] Uploading timelapse video (early finalize) to YouTube...");
									var titleName = lastJobFilename ?? sessionToFinalize;
									var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, titleName!, CancellationToken.None);
									if (!string.IsNullOrWhiteSpace(videoId))
									{
										Console.WriteLine($"[Timelapse] Early-upload complete: https://www.youtube.com/watch?v={videoId}");
									}
								}
								catch (Exception upx)
								{
									Console.WriteLine($"[Timelapse] Early-upload failed: {upx.Message}");
								}
							}
						}
						catch (Exception fex)
						{
							Console.WriteLine($"[Timelapse] Early finalize failed: {fex.Message}");
						}
					}, CancellationToken.None);

					// Clear the active session reference so end-of-print path won't double-run
					activeTimelapseSessionName = null;
				}
			}
			else if (!isPrinting && (streamCts != null || streamTask != null))
			{
				// Job finished, end stream and finalize timelapse
				Console.WriteLine($"[Watcher] Print job finished: {lastJobFilename}");
				// Preserve the filename we detected at job start for upload metadata
				var finishedJobFilename = lastJobFilename;
				lastCompletedJobFilename = finishedJobFilename;
				lastJobFilename = null;
				if (streamCts != null)
				{
					try { streamCts.Cancel(); } catch { }
					if (streamTask != null) await streamTask;
					streamCts = null;
					streamTask = null;
				}
				if (timelapseCts != null)
				{
					try { timelapseCts.Cancel(); } catch { }
					if (timelapseTask != null)
					{
						try { await timelapseTask; } catch (OperationCanceledException) { /* Expected */ }
					}
					timelapseCts = null;
					timelapseTask = null;
				}
				if (timelapseFinalizeTask != null)
				{
					// Early finalize already started; wait for it to complete
					try { await timelapseFinalizeTask; } catch { }
					timelapseFinalizeTask = null;
				}
				else if (activeTimelapseSessionName != null)
				{
					Console.WriteLine($"[Timelapse] Stopping timelapse session (end of print): {activeTimelapseSessionName}");
					try
					{
						var createdVideoPath = await timelapseManager!.StopTimelapseAsync(activeTimelapseSessionName);
						
						// Upload the timelapse video to YouTube if enabled and video was created successfully
						if (!string.IsNullOrWhiteSpace(createdVideoPath) && File.Exists(createdVideoPath))
						{
							var uploadTimelapse = config.GetValue<bool?>("Timelapse:Upload") ?? false;
							if (uploadTimelapse && ytService != null)
							{
								Console.WriteLine("[Timelapse] Uploading timelapse video to YouTube...");
								try
								{
									// Use the timelapse folder name (sanitized) for nicer titles
									var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, activeTimelapseSessionName, CancellationToken.None);
									if (!string.IsNullOrWhiteSpace(videoId))
									{
										Console.WriteLine($"[Timelapse] Video uploaded successfully! https://www.youtube.com/watch?v={videoId}");
										try
										{
											var playlistName = config.GetValue<string>("YouTube:Playlist:Name");
											if (!string.IsNullOrWhiteSpace(playlistName))
											{
												var playlistPrivacy = config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted";
												var pid = await ytService.EnsurePlaylistAsync(playlistName, playlistPrivacy, CancellationToken.None);
												if (!string.IsNullOrWhiteSpace(pid))
												{
													await ytService.AddVideoToPlaylistAsync(pid, videoId, CancellationToken.None);
												}
											}
										}
										catch (Exception ex)
										{
											Console.WriteLine($"[YouTube] Failed to add timelapse to playlist: {ex.Message}");
										}
									}
								}
								catch (Exception ex)
								{
									Console.WriteLine($"[Timelapse] Failed to upload video to YouTube: {ex.Message}");
								}
							}
							else if (!uploadTimelapse)
							{
								Console.WriteLine("[Timelapse] Video upload to YouTube is disabled (Timelapse:Upload=false)");
							}
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[Timelapse] Failed to create video: {ex.Message}");
					}
					activeTimelapseSessionName = null;
				}
			}

			// Adaptive polling: poll faster when we're near completion (must be inside try block)
			pollInterval = basePollInterval;
			if (isPrinting && timelapse != null && !lastLayerTriggered)
			{
				// Use fast polling if:
				// - Less than 2 minutes remaining
				// - More than 95% complete
				// - Within 5 layers of completion
				bool nearCompletion = (remaining.HasValue && remaining.Value <= TimeSpan.FromMinutes(2)) ||
				                      (progressPct.HasValue && progressPct.Value >= 95.0) ||
				                      (currentLayer.HasValue && totalLayers.HasValue && totalLayers.Value > 0 && 
				                       currentLayer.Value >= (totalLayers.Value - 5));
				if (nearCompletion)
				{
					pollInterval = fastPollInterval;
					Console.WriteLine($"[Watcher] Using fast polling ({pollInterval.TotalSeconds}s) - near completion");
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Watcher] Error: {ex.Message}");
		}

		await Task.Delay(pollInterval, cancellationToken);
		}
	}
	catch (OperationCanceledException)
	{
		Console.WriteLine("[Watcher] Polling cancelled by user.");
	}
	catch (Exception ex)
	{
		Console.WriteLine($"[Watcher] Unexpected error: {ex.Message}");
	}
	finally
	{
		// Cleanup on exit
		Console.WriteLine("[Watcher] Shutting down...");
		if (streamCts != null)
		{
			try { streamCts.Cancel(); } catch { }
			if (streamTask != null)
			{
				try { await streamTask; } catch { }
			}
		}
		if (timelapseCts != null)
		{
			try { timelapseCts.Cancel(); } catch { }
			if (timelapseTask != null)
			{
				try { await timelapseTask; } catch (OperationCanceledException) { /* Expected */ }
			}
		}
		if (timelapse != null)
		{
			Console.WriteLine($"[Timelapse] Creating video from {timelapse.OutputDir}...");
			var folderName = Path.GetFileName(timelapse.OutputDir);
			var videoPath = Path.Combine(timelapse.OutputDir, $"{folderName}.mp4");
			try
			{
				var createdVideoPath = await timelapse.CreateVideoAsync(videoPath, 30, CancellationToken.None);
				
				// Upload the timelapse video to YouTube if enabled and video was created successfully
				if (!string.IsNullOrWhiteSpace(createdVideoPath) && File.Exists(createdVideoPath))
				{
					var uploadTimelapse = config.GetValue<bool?>("Timelapse:Upload") ?? false;
					if (uploadTimelapse && ytService != null)
					{
						Console.WriteLine("[Timelapse] Uploading timelapse video to YouTube...");
						try
						{
							// Prefer the recently finished job's filename; fallback to timelapse folder name
							var filenameForUpload = lastCompletedJobFilename ?? lastJobFilename ?? folderName;
							var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, filenameForUpload, CancellationToken.None);
								if (!string.IsNullOrWhiteSpace(videoId))
								{
									Console.WriteLine($"[Timelapse] Video uploaded successfully! https://www.youtube.com/watch?v={videoId}");
									// Add to playlist if configured
									try
									{
										var playlistName = config.GetValue<string>("YouTube:Playlist:Name");
										if (!string.IsNullOrWhiteSpace(playlistName))
										{
											var playlistPrivacy = config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted";
											var pid = await ytService.EnsurePlaylistAsync(playlistName, playlistPrivacy, CancellationToken.None);
											if (!string.IsNullOrWhiteSpace(pid))
											{
												await ytService.AddVideoToPlaylistAsync(pid, videoId, CancellationToken.None);
											}
										}
									}
									catch (Exception ex)
									{
										Console.WriteLine($"[YouTube] Failed to add timelapse to playlist: {ex.Message}");
									}
								}
						}
						catch (Exception ex)
						{
							Console.WriteLine($"[Timelapse] Failed to upload video to YouTube: {ex.Message}");
						}
					}
					else if (!uploadTimelapse)
					{
						Console.WriteLine("[Timelapse] Video upload to YouTube is disabled (Timelapse:Upload=false)");
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Timelapse] Failed to create video: {ex.Message}");
			}
			timelapse.Dispose();
		}
		
		// Cleanup services
		ytService?.Dispose();
		timelapseManager?.Dispose();
		
		Console.WriteLine("[Watcher] Cleanup complete.");
	}
}

static async Task StartYouTubeStreamAsync(IConfiguration config, string source, string? manualKey, CancellationToken cancellationToken, bool enableTimelapse = true, PrintStreamer.Overlay.ITimelapseMetadataProvider? timelapseProvider = null)
{
	string? rtmpUrl = null;
	string? streamKey = null;
	string? broadcastId = null;
	string? moonrakerFilename = null;
	YouTubeControlService? ytService = null;
	TimelapseService? timelapse = null;
	CancellationTokenSource? timelapseCts = null;
	Task? timelapseTask = null;
	IStreamer? streamer = null;
	PrintStreamer.Overlay.OverlayTextService? overlayService = null;

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
			moonrakerFilename = result.filename;

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

			// Ensure and add broadcast to playlist if configured
			try
			{
				var playlistName = config.GetValue<string>("YouTube:Playlist:Name");
				if (!string.IsNullOrWhiteSpace(playlistName))
				{
					var playlistPrivacy = config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted";
					var pid = await ytService.EnsurePlaylistAsync(playlistName, playlistPrivacy, cancellationToken);
					if (!string.IsNullOrWhiteSpace(pid) && !string.IsNullOrWhiteSpace(broadcastId))
					{
						await ytService.AddVideoToPlaylistAsync(pid, broadcastId, cancellationToken);
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[YouTube] Failed to add broadcast to playlist: {ex.Message}");
			}

		// Upload initial thumbnail for the broadcast
		try
		{
			Console.WriteLine("[Thumbnail] Capturing initial thumbnail...");
			var initialThumbnail = await FetchSingleJpegFrameAsync(source, 10, cancellationToken);
			if (initialThumbnail != null && !string.IsNullOrWhiteSpace(broadcastId))
			{
				var ok = await ytService.SetBroadcastThumbnailAsync(broadcastId, initialThumbnail, cancellationToken);
				if (ok)
					Console.WriteLine($"[Thumbnail] Initial thumbnail uploaded successfully");
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Thumbnail] Failed to upload initial thumbnail: {ex.Message}");
		}

		// Start timelapse service for stream mode (only if enabled)
		if (enableTimelapse)
		{
			var mainTlDir = config.GetValue<string>("Timelapse:MainFolder") ?? Path.Combine(Directory.GetCurrentDirectory(), "timelapse");
			// Use filename from Moonraker if available, otherwise use timestamp
			string streamId;
			if (!string.IsNullOrWhiteSpace(moonrakerFilename))
			{
				// Use just the filename for consistency with poll mode
				var filenameSafe = SanitizeFilename(moonrakerFilename);
				streamId = filenameSafe;
				Console.WriteLine($"[Timelapse] Using filename from Moonraker: {moonrakerFilename}");
			}
			else
			{
				streamId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
				Console.WriteLine($"[Timelapse] No filename from Moonraker, using timestamp only");
			}
			timelapse = new TimelapseService(mainTlDir, streamId);

			// Capture immediate first frame for timelapse
			Console.WriteLine($"[Timelapse] Capturing initial frame...");
			try
			{
				var initialFrame = await FetchSingleJpegFrameAsync(source, 10, cancellationToken);
				if (initialFrame != null)
				{
					await timelapse.SaveFrameAsync(initialFrame, cancellationToken);
					Console.WriteLine($"[Timelapse] Initial frame captured successfully");
				}
				else
				{
					Console.WriteLine($"[Timelapse] Warning: Failed to capture initial frame");
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Timelapse] Error capturing initial frame: {ex.Message}");
			}

			timelapseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var timelapsePeriod = config.GetValue<TimeSpan?>("Timelapse:Period") ?? TimeSpan.FromMinutes(1);
			timelapseTask = Task.Run(async () =>
			{
				while (!timelapseCts.Token.IsCancellationRequested)
				{
					try
					{
						var frame = await FetchSingleJpegFrameAsync(source, 10, timelapseCts.Token);
						if (frame != null)
						{
							await timelapse.SaveFrameAsync(frame, timelapseCts.Token);
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Timelapse frame error: {ex.Message}");
					}
					await Task.Delay(timelapsePeriod, timelapseCts.Token);
				}
			}, timelapseCts.Token);
		}
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
		
		var targetFps = config.GetValue<int?>("Stream:TargetFps") ?? 6;
		var bitrateKbps = config.GetValue<int?>("Stream:BitrateKbps") ?? 800;

		if (useNativeStreamer)
		{
			Console.WriteLine($"Starting native .NET streamer to {rtmpUrl}/*** (fps={targetFps}, kbps={bitrateKbps})");
			#pragma warning disable CS0618 // Suppress obsolete warning for experimental native streamer
			streamer = new MjpegToRtmpStreamer(source, fullRtmpUrl, targetFps, bitrateKbps);
			#pragma warning restore CS0618
		}
		else
		{
			Console.WriteLine($"Starting ffmpeg streamer to {rtmpUrl}/*** (fps={targetFps}, kbps={bitrateKbps})");
			// Setup optional overlay
			FfmpegOverlayOptions? overlayOptions = null;
					if (config.GetValue<bool?>("Overlay:Enabled") ?? false)
			{
				try
				{
							overlayService = new PrintStreamer.Overlay.OverlayTextService(config, timelapseProvider);
					overlayService.Start();
					overlayOptions = new FfmpegOverlayOptions
					{
						TextFile = overlayService.TextFilePath,
						FontFile = config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
						FontSize = config.GetValue<int?>("Overlay:FontSize") ?? 22,
						FontColor = config.GetValue<string>("Overlay:FontColor") ?? "white",
						Box = config.GetValue<bool?>("Overlay:Box") ?? true,
						BoxColor = config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4",
						BoxBorderW = config.GetValue<int?>("Overlay:BoxBorderW") ?? 8,
						X = config.GetValue<string>("Overlay:X") ?? "(w-tw)-20",
						Y = config.GetValue<string>("Overlay:Y") ?? "20"
					};
					Console.WriteLine($"[Overlay] Enabled drawtext overlay from {overlayOptions.TextFile}");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[Overlay] Failed to start overlay service: {ex.Message}");
				}
			}

			streamer = new FfmpegStreamer(source, fullRtmpUrl, targetFps, bitrateKbps, overlayOptions);
		}
		
		// Start streamer without awaiting so we can detect ingestion while it's running
		var streamerStartTask = streamer.StartAsync(cancellationToken);

		// Ensure streamer is force-stopped when cancellation is requested (extra safety)
		using var stopOnCancel = cancellationToken.Register(() =>
		{
			try
			{
				Console.WriteLine("Cancellation requested — stopping streamer...");
				streamer?.Stop();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error stopping streamer on cancel: {ex.Message}");
			}
		});

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
		try { overlayService?.Dispose(); } catch { }
		// Final thumbnail upload removed: do not capture/upload a final thumbnail automatically
		
		// Stop timelapse task and create video (only if enabled)
		if (enableTimelapse)
		{
			if (timelapseCts != null)
			{
				try { timelapseCts.Cancel(); } catch { }
				if (timelapseTask != null)
				{
					try { await timelapseTask; } catch (OperationCanceledException) { /* Expected */ }
				}
				timelapseCts = null;
				timelapseTask = null;
			}
			if (timelapse != null)
			{
				Console.WriteLine($"[Timelapse] Creating video from {timelapse.OutputDir}...");
				var folderName = Path.GetFileName(timelapse.OutputDir);
				var videoPath = Path.Combine(timelapse.OutputDir, $"{folderName}.mp4");
				// Use a new cancellation token for video creation (don't use the cancelled one)
				try
				{
					var createdVideoPath = await timelapse.CreateVideoAsync(videoPath, 30, CancellationToken.None);

					// Upload the timelapse video to YouTube if enabled and video was created successfully
					if (!string.IsNullOrWhiteSpace(createdVideoPath) && File.Exists(createdVideoPath))
					{
						var uploadTimelapse = config.GetValue<bool?>("Timelapse:Upload") ?? false;
						if (uploadTimelapse && ytService != null)
						{
							Console.WriteLine("[Timelapse] Uploading timelapse video to YouTube...");
							try
							{
								// Use moonrakerFilename if available (from CreateLiveBroadcastAsync), otherwise extract from timelapse folder name
								var filenameForUpload = moonrakerFilename ?? Path.GetFileName(timelapse?.OutputDir);
								var videoId = await ytService.UploadTimelapseVideoAsync(createdVideoPath, filenameForUpload, CancellationToken.None);
								if (!string.IsNullOrWhiteSpace(videoId))
								{
									Console.WriteLine($"[Timelapse] Video uploaded successfully! https://www.youtube.com/watch?v={videoId}");
								}
							}
							catch (Exception ex)
							{
								Console.WriteLine($"[Timelapse] Failed to upload video to YouTube: {ex.Message}");
							}
						}
						else if (!uploadTimelapse)
						{
							Console.WriteLine("[Timelapse] Video upload to YouTube is disabled (Timelapse:Upload=false)");
						}
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[Timelapse] Failed to create video: {ex.Message}");
				}
				timelapse?.Dispose();
				timelapse = null;
			}
		}
		// Ensure streamer is stopped (in case cancellation didn't trigger it for some reason)
		try { streamer?.Stop(); } catch { }

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
