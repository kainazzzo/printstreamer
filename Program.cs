using PrintStreamer.Timelapse;
using PrintStreamer.Services;

// Moonraker polling and streaming helpers moved to Services/MoonrakerPoller.cs

// PrintStreamer - Stream 3D printer webcam to YouTube Live
// Configuration is loaded from appsettings.json, environment variables, and command-line arguments.
//
// Usage examples:
//   dotnet run -- --Stream:Source "http://printer.local/webcam/?action=stream"
//
// Environment variables:
//   export Stream__Source="http://printer.local/webcam/?action=stream"
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


// New: allow serving the web UI by default; set Serve:Enabled=false to disable
var serveEnabled = config.GetValue<bool?>("Serve:Enabled") ?? true;

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

// The application always runs the host and the hosted poller. Diagnostic/test modes were removed.

// Ensure the WebApplication host is built and run in all modes so IHostedService instances start
// Configure Kestrel only when we're serving HTTP
if (serveEnabled)
{
	webBuilder.WebHost.ConfigureKestrel(options => { options.ListenAnyIP(8080); });
	// Reduce shutdown timeout to respond faster to Ctrl+C (default is 30 seconds)
	webBuilder.Host.ConfigureHostOptions(opts => { opts.ShutdownTimeout = TimeSpan.FromSeconds(3); });
}

// Expose stream/task variables for shutdown handling
Task? streamTask = null;
CancellationTokenSource? streamCts = null;
TimelapseManager? timelapseManager = null;

var app = webBuilder.Build();

if (serveEnabled)
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

	// If OAuth or stream key is provided, optionally start YouTube streaming in the background when configured
	var startYoutubeInServe = config.GetValue<bool?>("YouTube:StartInServe") ?? false;
	if ((useOAuth || !string.IsNullOrWhiteSpace(key)) && startYoutubeInServe)
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

// (Local HLS preview startup will be handled in the Serve static-file block below so we only declare the variables once.)

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

	// Live control endpoint: start broadcast (promote current encoder to live)
	app.MapPost("/api/live/start", async (HttpContext ctx) =>
	{
		try
		{
			var (ok, message) = await MoonrakerPoller.StartBroadcastAsync(config, ctx.RequestAborted);
			if (ok) return Results.Json(new { success = true });
			return Results.Json(new { success = false, error = message });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	app.MapGet("/api/live/status", (HttpContext ctx) =>
	{
		try
		{
			var isLive = MoonrakerPoller.IsBroadcastActive;
			return Results.Json(new { isLive });
		}
		catch (Exception ex)
		{
			return Results.Json(new { isLive = false, error = ex.Message });
		}
	});

	app.MapPost("/api/live/stop", async (HttpContext ctx) =>
	{
		try
		{
			var (ok, message) = await MoonrakerPoller.StopBroadcastAsync(config, ctx.RequestAborted);
			if (ok) return Results.Json(new { success = true });
			return Results.Json(new { success = false, error = message });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
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
		var htmlPath = Path.Combine(Directory.GetCurrentDirectory(), "http", "index.html");
		if (File.Exists(htmlPath))
		{
			await ctx.Response.SendFileAsync(htmlPath);
		}
		else
		{
			ctx.Response.StatusCode = 404;
			await ctx.Response.WriteAsync("Control panel HTML not found.");
		}
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

	// Serve local HLS output if configured
	var localStreamEnabled = config.GetValue<bool?>("Stream:Local:Enabled") ?? false;
	var hlsFolderRaw = config.GetValue<string>("Stream:Local:HlsFolder");
	// default to ./hls but ensure we use an absolute path for the file provider
	var hlsFolder = Path.GetFullPath(hlsFolderRaw ?? Path.Combine(Directory.GetCurrentDirectory(), "hls"));
	if (localStreamEnabled)
	{
		Console.WriteLine($"[Serve] Serving local HLS at /hls from {hlsFolder}");
		Directory.CreateDirectory(hlsFolder);

		// Wipe any existing HLS files on startup so we start with a clean slate.
		try
		{
			if (Directory.Exists(hlsFolder))
			{
				foreach (var f in Directory.EnumerateFiles(hlsFolder))
				{
					try { File.Delete(f); } catch { }
				}
				foreach (var d in Directory.EnumerateDirectories(hlsFolder))
				{
					try { Directory.Delete(d, true); } catch { }
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[HLS] Failed to wipe HLS folder on startup: {ex.Message}");
		}

		// Start a background cleanup task to keep the HLS folder tidy.
		// Configurable options:
		// Stream:Local:HlsMaxSegments (int) - keep at most this many segment files (default 20)
		// Stream:Local:HlsCleanupIntervalSeconds (int) - cleanup interval in seconds (default 10)
		var hlsMaxSegments = config.GetValue<int?>("Stream:Local:HlsMaxSegments") ?? 20;
		var hlsCleanupInterval = config.GetValue<int?>("Stream:Local:HlsCleanupIntervalSeconds") ?? 10;

		_ = Task.Run(async () =>
		{
			try
			{
				while (!appCts.IsCancellationRequested)
				{
					try
					{
						if (Directory.Exists(hlsFolder))
						{
							// Collect segment files (seg_*.ts) and manifest files (*.m3u8)
							var segFiles = Directory.GetFiles(hlsFolder, "seg_*.ts").Select(p => new FileInfo(p)).OrderByDescending(f => f.CreationTimeUtc).ToArray();
							if (segFiles.Length > hlsMaxSegments)
							{
								// Keep newest hlsMaxSegments and delete the rest
								var toDelete = segFiles.Skip(hlsMaxSegments).ToArray();
								foreach (var f in toDelete)
								{
									try { File.Delete(f.FullName); } catch { }
								}
							}
							// Also remove any orphaned temporary files or old manifests older than a minute
							var oldFiles = Directory.GetFiles(hlsFolder).Select(p => new FileInfo(p)).Where(fi => (DateTime.UtcNow - fi.CreationTimeUtc).TotalSeconds > 60).ToArray();
							foreach (var of in oldFiles)
							{
								if (of.Name.StartsWith("seg_") || of.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || of.Extension.Equals(".old", StringComparison.OrdinalIgnoreCase))
								{
									try { File.Delete(of.FullName); } catch { }
								}
							}
						}
					}
					catch { }
					await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, hlsCleanupInterval)), appCts.Token).ContinueWith(_ => { });
				}
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				Console.WriteLine($"[HLS Cleanup] Error: {ex.Message}");
			}
		});
		app.UseStaticFiles(new StaticFileOptions
		{
			FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(hlsFolder),
			RequestPath = "/hls",
			ServeUnknownFileTypes = true
		});

		// Note: local HLS preview will be produced by the same ffmpeg process that streams to YouTube
		// when Stream:Local:Enabled is true. We intentionally avoid running a second encoder process.
	}

	// Fallback HLS endpoint: serve manifests and segments from the HLS folder even
	// if static files middleware doesn't handle them in some hosting scenarios.
	app.MapGet("/hls/{**path}", async (string path, HttpContext ctx) =>
	{
		try
		{
			// Normalize path: remove any leading directory separators so Path.Combine treats it as relative
			if (string.IsNullOrWhiteSpace(path)) path = "stream.m3u8";
			path = path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\');

			var requested = Path.GetFullPath(Path.Combine(hlsFolder, path));
			var folderFull = Path.GetFullPath(hlsFolder);
			if (!requested.StartsWith(folderFull))
			{
				Console.WriteLine($"[HLS] Forbidden path traversal attempt: {path} -> {requested}");
				ctx.Response.StatusCode = 403;
				return;
			}

			if (!File.Exists(requested))
			{
				Console.WriteLine($"[HLS] Not found: {requested}");
				ctx.Response.StatusCode = 404;
				return;
			}

			// Set content type for HLS manifests and ts segments
			var ext = Path.GetExtension(requested).ToLowerInvariant();
			switch (ext)
			{
				case ".m3u8": ctx.Response.ContentType = "application/vnd.apple.mpegurl"; break;
				case ".ts": ctx.Response.ContentType = "video/MP2T"; break;
				default: ctx.Response.ContentType = "application/octet-stream"; break;
			}

			await ctx.Response.SendFileAsync(requested);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[HLS] Serve error: {ex.Message}");
			ctx.Response.StatusCode = 500;
		}
	});

		// Debug endpoint: list files in the configured HLS folder (useful to diagnose 404s)
		app.MapGet("/api/hls/list", (HttpContext ctx) =>
		{
			try
			{
				if (!localStreamEnabled)
				{
					return Results.Json(new { enabled = false, files = Array.Empty<string>() });
				}
				var files = Directory.Exists(hlsFolder)
					? Directory.EnumerateFiles(hlsFolder).Select(p => Path.GetFileName(p)).ToArray()
					: Array.Empty<string>();
				return Results.Json(new { enabled = true, folder = hlsFolder, files });
			}
			catch (Exception ex)
			{
				return Results.Json(new { enabled = localStreamEnabled, error = ex.Message });
			}
		});

}

// Start the host so IHostedService instances (MoonrakerHostedService) are started in all modes
await app.RunAsync();

// If serve UI was enabled, wait for the background stream task (if any) to complete and clean up
if (serveEnabled)
{
    if (streamTask != null)
    {
        await streamTask;
    }
    timelapseManager?.Dispose();
}
// Poll/stream behavior is handled by MoonrakerHostedService when configured

// Note: test-mode helper removed. The app now always runs the host and poller.

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
	Console.WriteLine("  (Mode option removed) The application now always polls Moonraker and serves the UI by default. Use Serve:Enabled=false to disable the UI.");
	Console.WriteLine("  Stream:Source                     - MJPEG URL (required)");
	Console.WriteLine("  Stream:UseNativeStreamer          - true/false (default: false)");
	Console.WriteLine("  YouTube:Key                       - Manual stream key (optional)");
	Console.WriteLine("  YouTube:OAuth:ClientId            - OAuth client ID (optional)");
	Console.WriteLine("  YouTube:OAuth:ClientSecret        - OAuth client secret (optional)");
	Console.WriteLine();
	Console.WriteLine("Examples:");
	Console.WriteLine();
	Console.WriteLine("  # Run with defaults from appsettings.json");
	Console.WriteLine("  dotnet run");
	Console.WriteLine();
	Console.WriteLine("  # Set multiple options on command-line");
	Console.WriteLine("  dotnet run -- --Stream:Source \"http://printer/webcam/?action=stream\"");
	Console.WriteLine();
	Console.WriteLine("  # Use environment variables");
	Console.WriteLine("  export Stream__Source=\"http://printer/webcam/?action=stream\"");
	Console.WriteLine("  export Stream__UseNativeStreamer=true");
	Console.WriteLine("  dotnet run");
	Console.WriteLine();
	Console.WriteLine("  # Mix config file, env vars, and command-line");
	Console.WriteLine("  # (Command-line > Env vars > appsettings.json)");
	Console.WriteLine("  dotnet run -- --YouTube:Key \"your-key\"");
	Console.WriteLine();
	Console.WriteLine("See README.md for complete documentation.");
	Console.WriteLine();
}