using PrintStreamer.Timelapse;
using PrintStreamer.Services;
using PrintStreamer.Streamers;
using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Antiforgery;
using PrintStreamer.Overlay;

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

// Get a logger for startup operations (before the app is built)
using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var startupLogger = loggerFactory.CreateLogger("PrintStreamer.Startup");

// Load custom configuration file (the filename itself comes from the config we just loaded)
// This allows user-modified settings to be persisted to a separate file
var customConfigFile = webBuilder.Configuration.GetValue<string>("CustomConfigFile");
if (!string.IsNullOrWhiteSpace(customConfigFile))
{
	var customConfigPath = Path.Combine(Directory.GetCurrentDirectory(), customConfigFile);
	startupLogger.LogInformation("Loading custom configuration from: {ConfigFile}", customConfigFile);

	// Add the custom config file to the configuration builder
	// It will override values from appsettings.json if they exist
	webBuilder.Configuration.AddJsonFile(customConfigFile, optional: true, reloadOnChange: true);
}

var config = webBuilder.Configuration;

// If a client secrets file path is configured, add a configuration provider that loads
// the client_id and client_secret from that JSON file into configuration keys
try
{
	var csPath = webBuilder.Configuration.GetValue<string>("YouTube:OAuth:ClientSecretsFilePath");
	if (!string.IsNullOrWhiteSpace(csPath))
	{
		// Use absolute path as-is, or combine with current directory if relative
		var fullPath = Path.IsPathRooted(csPath) ? csPath : Path.Combine(Directory.GetCurrentDirectory(), csPath);
		startupLogger.LogInformation("Checking for client secrets file at: {Path}", fullPath);
		// Register our custom provider (optional: true so missing file won't crash)
		((IConfigurationBuilder)webBuilder.Configuration).Add(new PrintStreamer.Utils.ClientSecretsConfigurationSource(fullPath, optional: true, logger: startupLogger));
	}
}
catch (Exception ex)
{
	startupLogger.LogWarning(ex, "Failed to register client secrets configuration provider");
}

// Persist ASP.NET Core DataProtection keys so antiforgery cookies survive container restarts
try
{
	var dpKeyDir = config.GetValue<string>("DataProtection:KeyDirectory") ?? "//dpkeys";
	Directory.CreateDirectory(dpKeyDir);
	webBuilder.Services
		.AddDataProtection()
		.PersistKeysToFileSystem(new DirectoryInfo(dpKeyDir))
		.SetApplicationName("PrintStreamer");
	startupLogger.LogInformation("DataProtection key ring at: {KeyDirectory}", dpKeyDir);
}
catch (Exception ex)
{
	startupLogger.LogWarning(ex, "Could not initialize DataProtection key persistence");
}

// Configure antiforgery cookie with a stable, known name so we can refresh it on invalid states
webBuilder.Services.AddAntiforgery(options =>
{
	options.Cookie.Name = "printstreamer.AntiForgery";
	options.Cookie.HttpOnly = true;
	options.HeaderName = "X-CSRF-TOKEN";
	options.SuppressXFrameOptionsHeader = true; // We proxy iframes explicitly
});

// Generate fallback_black.jpg at startup if it doesn't exist
// This file is used by WebCamManager when the camera source is unavailable
var fallbackImagePath = Path.Combine(Directory.GetCurrentDirectory(), "fallback_black.jpg");
if (!File.Exists(fallbackImagePath))
{
	try
	{
		startupLogger.LogInformation("Generating fallback_black.jpg...");
		var psi = new ProcessStartInfo
		{
			FileName = "ffmpeg",
			Arguments = "-f lavfi -i color=c=black:s=640x480:d=1 -frames:v 1 -y fallback_black.jpg",
			WorkingDirectory = Directory.GetCurrentDirectory(),
			UseShellExecute = false,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			CreateNoWindow = true
		};
		using var proc = Process.Start(psi);
		if (proc != null)
		{
			await proc.WaitForExitAsync();
			if (proc.ExitCode == 0)
			{
				startupLogger.LogInformation("fallback_black.jpg created successfully");
			}
			else
			{
				startupLogger.LogWarning("ffmpeg exited with code {ExitCode} when creating fallback image", proc.ExitCode);
			}
		}
	}
	catch (Exception ex)
	{
		startupLogger.LogWarning(ex, "Failed to generate fallback_black.jpg");
	}
}

// Register application services
webBuilder.Services.AddSingleton<MoonrakerClient>();
webBuilder.Services.AddSingleton<TimelapseManager>();
webBuilder.Services.AddSingleton<ITimelapseManager, TimelapseManager>();
// Expose TimelapseManager as ITimelapseMetadataProvider for overlay text enrichment
webBuilder.Services.AddSingleton<ITimelapseMetadataProvider, TimelapseManager>();
// YouTube API polling manager with configuration
webBuilder.Services.Configure<YouTubePollingOptions>(
	webBuilder.Configuration.GetSection(YouTubePollingOptions.SectionName));
webBuilder.Services.AddSingleton<YouTubePollingManager>();
// YouTube API client (singleton to avoid repeated authentication and instance creation)
webBuilder.Services.AddSingleton<YouTubeControlService>();
webBuilder.Services.AddSingleton<WebCamManager>();
webBuilder.Services.AddSingleton<StreamService>();
webBuilder.Services.AddSingleton<StreamOrchestrator>();
webBuilder.Services.AddSingleton<PrintStreamOrchestrator>();
webBuilder.Services.AddSingleton<MoonrakerPollerService>();
webBuilder.Services.AddHostedService<MoonrakerHostedService>();
webBuilder.Services.AddSingleton<AudioService>();
webBuilder.Services.AddSingleton<AudioBroadcastService>();
// Printer console service (skeleton)
webBuilder.Services.AddSingleton<PrinterConsoleService>();
// Start the same singleton as a hosted service
webBuilder.Services.AddHostedService(sp => sp.GetRequiredService<PrinterConsoleService>());
// Overlay text generator (reads Moonraker, writes text for ffmpeg drawtext)
webBuilder.Services.AddSingleton(sp =>
{
	var cfg = sp.GetRequiredService<IConfiguration>();
	var tl = sp.GetService<ITimelapseMetadataProvider>();
	var audio = sp.GetRequiredService<AudioService>();
	var overlayLogger = sp.GetRequiredService<ILogger<OverlayTextService>>();
	var moonrakerClient = sp.GetRequiredService<MoonrakerClient>();
	return new OverlayTextService(cfg, tl, () => audio.Current, overlayLogger, moonrakerClient);
});

// Add Blazor Server services
webBuilder.Services.AddRazorComponents()
	.AddInteractiveServerComponents();

// Register HttpClient with base address for API calls
webBuilder.Services.AddHttpClient<PrinterControlApiService>(client =>
{
	client.BaseAddress = new Uri("http://localhost:8080");
});

// Add controller endpoints for printer control API
webBuilder.Services.AddControllers();

// Read configuration values
string? source = config.GetValue<string>("Stream:Source");
// Only OAuth is supported for YouTube live broadcasts
string? oauthClientId = config.GetValue<string>("YouTube:OAuth:ClientId");
string? oauthClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret");

// New: allow serving the web UI by default; set Serve:Enabled=false to disable
var serveEnabled = config.GetValue<bool?>("Serve:Enabled") ?? true;

// Detect OAuth configuration for YouTube live broadcast creation
bool hasYouTubeOAuth = !string.IsNullOrWhiteSpace(oauthClientId) && !string.IsNullOrWhiteSpace(oauthClientSecret);

// Top-level cancellation token for graceful shutdown (propagated to streamers)
var appCts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
	startupLogger.LogInformation("Stopping (Ctrl+C)...");
	e.Cancel = true; // Prevent immediate termination
	try
	{
		appCts.Cancel();
	}
	catch (Exception ex)
	{
		startupLogger.LogError(ex, "Error during cancellation");
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

ProxyUtil.Logger = app.Services.GetRequiredService<ILogger<Program>>();
// Provide DI-created MoonrakerClient to the static poller so it can query printer state
try
{
	MoonrakerPoller.SetMoonrakerClient(app.Services.GetRequiredService<MoonrakerClient>());
}
catch
{
	// If registration fails for any reason, continue startup but log via proxy logger
	ProxyUtil.Logger?.LogWarning("Failed to register MoonrakerClient with MoonrakerPoller");
}
// Register DI-provided YouTubeControlService for use in the static poller helpers.
try
{
	MoonrakerPoller.SetYouTubeControlService(app.Services.GetRequiredService<YouTubeControlService>());
}
catch
{
	ProxyUtil.Logger?.LogWarning("Failed to register YouTubeControlService with MoonrakerPoller");
}

// Wire up audio track completion callback to orchestrator
{
	var orchestrator = app.Services.GetRequiredService<StreamOrchestrator>();
	var audioBroadcast = app.Services.GetRequiredService<AudioBroadcastService>();
	audioBroadcast.SetTrackFinishedCallback(() => orchestrator.OnAudioTrackFinishedAsync());
}

// Wire up PrintStreamOrchestrator to subscribe to PrinterState events from MoonrakerPoller
{
	var printStreamOrchestrator = app.Services.GetRequiredService<PrintStreamOrchestrator>();
	PrintStreamer.Services.MoonrakerPoller.PrintStateChanged += (prev, curr) =>
		_ = printStreamOrchestrator.HandlePrinterStateChangedAsync(prev, curr, CancellationToken.None);
}

// StreamOrchestrator is constructed and registered in DI. It should subscribe to
// MoonrakerPoller events or call Poller helpers as needed. Do not register the
// orchestrator with the poller here; keep inversion of control (poller -> event pub, orchestrator -> consumer).

// Get a logger for the application
var logger = app.Services.GetRequiredService<ILogger<Program>>();

// Add Blazor Server middleware
app.UseAntiforgery();

// Early middleware to proactively issue a fresh antiforgery cookie on GETs and clear stale ones
app.Use(async (ctx, next) =>
{
	try
	{
		var af = ctx.RequestServices.GetRequiredService<IAntiforgery>();
		if (string.Equals(ctx.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
		{
			// Issue/refresh the antiforgery cookie for this client; overwrites old/stale cookies
			af.GetAndStoreTokens(ctx);
		}
	}
	catch (AntiforgeryValidationException)
	{
		// If decryption failed (e.g., stale key), delete our known cookie and re-issue
		try { ctx.Response.Cookies.Delete("printstreamer.AntiForgery"); } catch { }
		try
		{
			var af = ctx.RequestServices.GetRequiredService<IAntiforgery>();
			af.GetAndStoreTokens(ctx);
		}
		catch { }
	}
	catch { }
	await next();
});

if (serveEnabled)
{
	// Enable WebSocket support (required for Mainsail/Fluidd)
	app.UseWebSockets();
	// Start ASP.NET Core minimal server to proxy the MJPEG source to clients on /stream
	if (string.IsNullOrWhiteSpace(source))
	{
		logger.LogError("Error: --source is required when running in --serve mode.");
		PrintHelp();
		return;
	}

	var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

	// Resolve managers from DI
	var webcamManager = app.Services.GetRequiredService<WebCamManager>();
	// Resolve timelapse manager from DI (registered earlier)
	timelapseManager = app.Services.GetRequiredService<TimelapseManager>();

	// Architecture Overview:
	// 1. WebCam Proxy (/stream) - Proxies MJPEG webcam, handles camera simulation
	// 2. Local Stream - ffmpeg reads /stream for encoding
	// 3. YouTube Live Broadcast - OAuth creates broadcast, restarts ffmpeg to add RTMP output

	app.MapGet("/stream", async (HttpContext ctx) => await webcamManager.HandleStreamRequest(ctx));
	app.MapGet("/stream/source", async (HttpContext ctx) => await webcamManager.HandleStreamRequest(ctx));
	// Overlay MJPEG endpoint: prefer the new IStreamer-based OverlayMjpegStreamer (per-request)
	// Fall back to the legacy OverlayMjpegManager if needed (kept registered for compatibility)
	var overlayTextSvc = app.Services.GetRequiredService<PrintStreamer.Overlay.OverlayTextService>();
	// Ensure overlay text writer is running
	try { overlayTextSvc.Start(); } catch { }

	app.MapGet("/stream/overlay", async (HttpContext ctx) =>
	{
		// Create a per-request streamer that depends on HttpContext
		try
		{
			var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
			var overlayText = ctx.RequestServices.GetRequiredService<PrintStreamer.Overlay.OverlayTextService>();
			var logger = ctx.RequestServices.GetRequiredService<ILogger<PrintStreamer.Streamers.OverlayMjpegStreamer>>();
			var streamer = new PrintStreamer.Streamers.OverlayMjpegStreamer(cfg, overlayText, ctx, logger);
			await streamer.StartAsync(ctx.RequestAborted);
			try { streamer.Dispose(); } catch { }
		}
		catch (Exception ex)
		{
			if (!ctx.Response.HasStarted)
			{
				ctx.Response.StatusCode = 500;
				await ctx.Response.WriteAsync("Overlay streamer error: " + ex.Message);
			}
		}
	});

	// List frames for a timelapse - returns JSON array of frame filenames in order
	app.MapGet("/api/timelapses/{name}/frames", (string name) =>
	{
		try
		{
			var timelapseDir = Path.Combine(timelapseManager.TimelapseDirectory, name);
			if (!Directory.Exists(timelapseDir)) return Results.Json(new { success = false, error = "Timelapse not found" });

			var frames = Directory.GetFiles(timelapseDir, "frame_*.jpg").OrderBy(f => f).Select(Path.GetFileName).ToArray();
			return Results.Json(new { success = true, frames });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Delete a single frame from a timelapse
	app.MapDelete("/api/timelapses/{name}/frames/{filename}", (string name, string filename, HttpContext ctx) =>
	{
		try
		{
			var timelapseDir = Path.Combine(timelapseManager.TimelapseDirectory, name);
			if (!Directory.Exists(timelapseDir)) return Results.Json(new { success = false, error = "Timelapse not found" });

			// Simple validation - disallow path separators
			if (filename.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0) return Results.Json(new { success = false, error = "Invalid filename" });

			var filePath = Path.Combine(timelapseDir, filename);
			if (!File.Exists(filePath)) return Results.Json(new { success = false, error = "File not found" });

			if (!filename.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return Results.Json(new { success = false, error = "Only frame .jpg files can be deleted" });

			// Prevent deletion while timelapse active
			if (timelapseManager.GetActiveSessionNames().Contains(name)) return Results.Json(new { success = false, error = "Cannot delete frames while timelapse is active" });

			File.Delete(filePath);

			// Reindex remaining frames to maintain contiguous numbering required by ffmpeg pattern
			var remaining = Directory.GetFiles(timelapseDir, "frame_*.jpg").OrderBy(f => f).ToArray();
			for (int i = 0; i < remaining.Length; i++)
			{
				var dst = Path.Combine(timelapseDir, $"frame_{i:D6}.jpg");
				var src = remaining[i];
				// If the filename already matches the desired index, skip
				if (string.Equals(Path.GetFileName(src), Path.GetFileName(dst), StringComparison.OrdinalIgnoreCase)) continue;
				// Overwrite destination if necessary
				try
				{
					if (File.Exists(dst)) File.Delete(dst);
					File.Move(src, dst);
				}
				catch { /* best-effort; ignore failures */ }
			}

			return Results.Json(new { success = true });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Mix video and audio streams into a single H.264+AAC output
	app.MapGet("/stream/mix", async (HttpContext ctx) =>
	{
		try
		{
			var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
			var logger = ctx.RequestServices.GetRequiredService<ILogger<PrintStreamer.Streamers.MixStreamer>>();
			var streamer = new PrintStreamer.Streamers.MixStreamer(config, ctx, logger);
			await streamer.StartAsync(ctx.RequestAborted);
			try { streamer.Dispose(); } catch { }
		}
		catch (Exception ex)
		{
			if (!ctx.Response.HasStarted)
			{
				ctx.Response.StatusCode = 500;
				await ctx.Response.WriteAsync("Mix streamer error: " + ex.Message);
			}
		}
	});

	// Helper function to capture single JPEG from a stream URL

	app.MapGet("/stream/overlay/coords", async (HttpContext ctx) =>
	{
		var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
		var overlayText = ctx.RequestServices.GetRequiredService<PrintStreamer.Overlay.OverlayTextService>();
		var fontSize = config.GetValue<int?>("Overlay:FontSize") ?? 16;
		var boxHeight = config.GetValue<int?>("Overlay:BoxHeight") ?? 75;
		var layout = OverlayLayout.Calculate(config, overlayText.TextFilePath, fontSize, boxHeight);
		var result = new
		{
			drawbox = new { x = layout.DrawboxX, y = layout.DrawboxY },
			text = new { x = layout.TextX, y = layout.TextY },
			layout.HasCustomX,
			layout.HasCustomY,
			layout.ApproxTextHeight,
			raw = new { x = layout.RawX, y = layout.RawY }
		};
		await ctx.Response.WriteAsJsonAsync(result);
	});
	async Task<bool> CaptureJpegFromStreamAsync(HttpContext ctx, string streamUrl, string name)
	{
		if (string.IsNullOrWhiteSpace(streamUrl))
		{
			ctx.Response.StatusCode = 503;
			await ctx.Response.WriteAsync($"{name} source not available");
			return false;
		}

		using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

		try
		{
			// Try snapshot action first if it's an HTTP URL
			if (Uri.TryCreate(streamUrl, UriKind.Absolute, out var srcUri))
			{
				var ub = new UriBuilder(srcUri);
				var query = System.Web.HttpUtility.ParseQueryString(ub.Query);
				query.Set("action", "snapshot");
				ub.Query = query.ToString();

				try
				{
					using var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, ub.Uri);
					using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
					using var resp = await httpClient.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token);
					if (resp.IsSuccessStatusCode)
					{
						var ct = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
						if (ct.Contains("jpeg") || ct.Contains("jpg") || ct.Contains("image"))
						{
							var bytes = await resp.Content.ReadAsByteArrayAsync();
							ctx.Response.StatusCode = 200;
							ctx.Response.ContentType = "image/jpeg";
							ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
							await ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, ctx.RequestAborted);
							return true;
						}
					}
				}
				catch { /* fall through to MJPEG parse */ }
			}

			// Fallback: parse JPEG from MJPEG stream
			using (var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, streamUrl))
			using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
			using (var resp = await httpClient.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token))
			{
				resp.EnsureSuccessStatusCode();
				using var s = await resp.Content.ReadAsStreamAsync(cts.Token);

				// Read JPEG from MJPEG stream by finding SOI (FFD8) and EOI (FFD9) markers
				var buffer = new byte[64 * 1024];
				using var ms = new MemoryStream();
				int bytesRead;
				bool foundSoi = false;
				int prev = -1;

				while ((bytesRead = await s.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
				{
					for (int i = 0; i < bytesRead; i++)
					{
						var b = buffer[i];
						// Check for SOI marker 0xFF 0xD8
						if (!foundSoi && prev == 0xFF && b == 0xD8)
						{
							foundSoi = true;
							ms.SetLength(0);
							ms.WriteByte(0xFF);
							ms.WriteByte(0xD8);
							prev = -1;
							continue;
						}

						if (foundSoi)
						{
							ms.WriteByte(b);
							// Check for EOI marker 0xFF 0xD9
							if (prev == 0xFF && b == 0xD9)
							{
								var frameBytes = ms.ToArray();
								ctx.Response.StatusCode = 200;
								ctx.Response.ContentType = "image/jpeg";
								ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
								await ctx.Response.Body.WriteAsync(frameBytes, 0, frameBytes.Length, ctx.RequestAborted);
								return true;
							}
						}

						prev = b;
					}
				}
			}

			// Failed to get a frame
			ctx.Response.StatusCode = 503;
			await ctx.Response.WriteAsync($"Failed to capture frame from {name}");
			return false;
		}
		catch (TimeoutException)
		{
			ctx.Response.StatusCode = 504;
			await ctx.Response.WriteAsync("Capture timeout");
			return false;
		}
		catch (Exception ex)
		{
			if (!ctx.Response.HasStarted)
			{
				ctx.Response.StatusCode = 502;
				await ctx.Response.WriteAsync("Capture error: " + ex.Message);
			}
			return false;
		}
	}

	// Stage 1: Single JPEG capture from raw source
	app.MapGet("/stream/source/capture", async (HttpContext ctx) =>
	{
		try
		{
			var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
			var source = config.GetValue<string>("Stream:Source") ?? "";
			await CaptureJpegFromStreamAsync(ctx, source, "stream source");
		}
		catch (OperationCanceledException) { }
	});

	// Stage 3: Single JPEG capture from overlayed stream
	app.MapGet("/stream/overlay/capture", async (HttpContext ctx) =>
	{
		try
		{
			await CaptureJpegFromStreamAsync(ctx, "http://127.0.0.1:8080/stream/overlay", "overlay stream");
		}
		catch (OperationCanceledException) { }
	});

	// Stage 5: Single JPEG capture from mixed stream (video+audio)
	app.MapGet("/stream/mix/capture", async (HttpContext ctx) =>
	{
		try
		{
			await CaptureJpegFromStreamAsync(ctx, "http://127.0.0.1:8080/stream/mix", "mix stream");
		}
		catch (OperationCanceledException) { }
	});

	// Serve the fallback black JPEG
	app.MapGet("/fallback_black.jpg", async (HttpContext ctx) =>
	{
		var fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), "fallback_black.jpg");
		if (File.Exists(fallbackPath))
		{
			ctx.Response.ContentType = "image/jpeg";
			await ctx.Response.SendFileAsync(fallbackPath);
		}
		else
		{
			ctx.Response.StatusCode = 404;
			await ctx.Response.WriteAsync("Fallback image not found");
		}
	});

	// Camera simulation control endpoints (useful for testing camera disconnects)
	// Camera simulation control endpoints (use WebCamManager as the canonical state)
	app.MapGet("/api/camera", (HttpContext ctx) =>
	{
		var webcamManager = ctx.RequestServices.GetRequiredService<WebCamManager>();
		return Results.Json(new { disabled = webcamManager.IsDisabled });
	});

	app.MapPost("/api/camera/on", async (HttpContext ctx, ILogger<Program> logger) =>
	{
		var webcamManager = ctx.RequestServices.GetRequiredService<WebCamManager>();
		var streamService = ctx.RequestServices.GetRequiredService<StreamService>();
		webcamManager.SetDisabled(false);
		logger.LogInformation("Camera simulation: enabled (camera on)");
		// Restart stream if one is active so ffmpeg picks up the new upstream availability
		if (streamService.IsStreaming)
		{
			try
			{
				await streamService.StopStreamAsync();
				await streamService.StartStreamAsync(null, null, ctx.RequestAborted);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Failed to restart stream");
			}
		}
		return Results.Json(new { disabled = webcamManager.IsDisabled });
	});

	app.MapPost("/api/camera/off", async (HttpContext ctx, ILogger<Program> logger) =>
	{
		var webcamManager = ctx.RequestServices.GetRequiredService<WebCamManager>();
		var streamService = ctx.RequestServices.GetRequiredService<StreamService>();
		webcamManager.SetDisabled(true);
		logger.LogInformation("Camera simulation: disabled (camera off)");
		// Restart stream if one is active so ffmpeg will read from the local proxy and therefore see the fallback frames
		if (streamService.IsStreaming)
		{
			try
			{
				await streamService.StopStreamAsync();
				await streamService.StartStreamAsync(null, null, ctx.RequestAborted);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Failed to restart stream");
			}
		}
		return Results.Json(new { disabled = webcamManager.IsDisabled });
	});

	app.MapPost("/api/camera/toggle", async (HttpContext ctx, ILogger<Program> logger) =>
	{
		var webcamManager = ctx.RequestServices.GetRequiredService<WebCamManager>();
		var streamService = ctx.RequestServices.GetRequiredService<StreamService>();
		webcamManager.Toggle();
		var newVal = webcamManager.IsDisabled;
		logger.LogInformation("Camera simulation: toggled -> disabled={IsDisabled}", newVal);
		// Restart stream if one is active
		if (streamService.IsStreaming)
		{
			try
			{
				await streamService.StopStreamAsync();
				await streamService.StartStreamAsync(null, null, ctx.RequestAborted);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Failed to restart stream");
			}
		}
		return Results.Json(new { disabled = newVal });
	});

	app.MapPost("/api/audio/enabled", async (HttpContext ctx, ILogger<Program> logger) =>
	{
		// Toggle persistent configuration flag for audio availability used by /stream/audio and internal streamers.
		var raw = ctx.Request.Query["enabled"].ToString();
		bool enabled;
		if (!bool.TryParse(raw, out enabled))
		{
			// Accept "1"/"0" and case-insensitive "true"/"false"
			enabled = raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
		}

		// Update in-memory configuration so subsequent requests read the new setting.
		config["Audio:Enabled"] = enabled.ToString();
		logger.LogInformation("Audio stream: {State}", enabled ? "enabled" : "disabled");

		// If a ffmpeg streamer is active, restart it so it re-reads the audio availability.
		try
		{
			var streamService = ctx.RequestServices.GetRequiredService<StreamService>();
			if (streamService.IsStreaming)
			{
				logger.LogInformation("Restarting active stream to pick up audio setting change");
				await streamService.StopStreamAsync();
				await streamService.StartStreamAsync(null, null, ctx.RequestAborted);
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to restart stream after audio toggle");
		}

		// Apply the audio-enabled change to the broadcaster so live audio stops/starts immediately
		try
		{
			var broadcaster = ctx.RequestServices.GetService<AudioBroadcastService>();
			broadcaster?.ApplyAudioEnabledState(enabled);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to apply audio toggle to broadcaster");
		}

		return Results.Json(new { success = true, enabled });
	});

	// Reverse proxy for Mainsail/Fluidd to bypass X-Frame-Options and same-origin issues
	app.MapGet("/proxy/mainsail/{**path}", async (HttpContext ctx, string? path, ILogger<Program> logger) =>
	{
		var target = config.GetValue<string>("PrinterUI:MainsailUrl");
		logger.LogDebug("GET /proxy/mainsail/{Path} -> target={Target}", path ?? "", target ?? "NOT CONFIGURED");
		if (string.IsNullOrWhiteSpace(target))
		{
			ctx.Response.StatusCode = 404;
			await ctx.Response.WriteAsync("Mainsail URL not configured");
			return;
		}
		await ProxyUtil.ProxyRequest(ctx, target, path ?? "");
	});

	app.MapGet("/proxy/fluidd/{**path}", async (HttpContext ctx, string? path, ILogger<Program> logger) =>
	{
		var target = config.GetValue<string>("PrinterUI:FluiddUrl");
		logger.LogDebug("GET /proxy/fluidd/{Path} -> target={Target}", path ?? "", target ?? "NOT CONFIGURED");
		if (string.IsNullOrWhiteSpace(target))
		{
			ctx.Response.StatusCode = 404;
			await ctx.Response.WriteAsync("Fluidd URL not configured");
			return;
		}
		await ProxyUtil.ProxyRequest(ctx, target, path ?? "");
	});

	// Also handle root without trailing catch-all
	app.MapGet("/proxy/mainsail", async (HttpContext ctx, ILogger<Program> logger) =>
	{
		var target = config.GetValue<string>("PrinterUI:MainsailUrl");
		if (string.IsNullOrWhiteSpace(target)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Mainsail URL not configured"); return; }
		logger.LogDebug("GET /proxy/mainsail -> {Target}", target);
		await ProxyUtil.ProxyRequest(ctx, target, "");
	});

	app.MapGet("/proxy/fluidd", async (HttpContext ctx, ILogger<Program> logger) =>
	{
		var target = config.GetValue<string>("PrinterUI:FluiddUrl");
		if (string.IsNullOrWhiteSpace(target)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Fluidd URL not configured"); return; }
		logger.LogDebug("GET /proxy/fluidd -> {Target}", target);
		await ProxyUtil.ProxyRequest(ctx, target, "");
	});

	// Some UI bundles emit absolute-root URLs like /assets/*, /img/*, /manifest.webmanifest, /sw.js.
	// We route these to the correct UI based on the Referer so they work under a prefix.
	var absRootMethods = new[] { "GET", "HEAD", "OPTIONS" };
	app.MapMethods("/assets/{**path}", absRootMethods, async (HttpContext ctx, string? path, ILogger<Program> logger) =>
	{
		var referer = ctx.Request.Headers.Referer.ToString();
		var fluidd = config.GetValue<string>("PrinterUI:FluiddUrl");
		var mainsail = config.GetValue<string>("PrinterUI:MainsailUrl");
		string? target = null;
		if (!string.IsNullOrWhiteSpace(referer) && referer.Contains("/mainsail", StringComparison.OrdinalIgnoreCase)) target = mainsail;
		else if (!string.IsNullOrWhiteSpace(referer) && referer.Contains("/fluidd", StringComparison.OrdinalIgnoreCase)) target = fluidd;
		// Fallback to Fluidd if ambiguous (both use similar asset paths)
		if (string.IsNullOrWhiteSpace(target)) target = fluidd ?? mainsail;
		if (string.IsNullOrWhiteSpace(target)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("UI target not configured"); return; }
		logger.LogDebug("ABS-ROOT /assets/{Path} via referer='{Referer}' -> {Target}", path ?? string.Empty, referer, target);
		await ProxyUtil.ProxyRequest(ctx, target, "assets/" + (path ?? string.Empty));
	});
	app.MapMethods("/img/{**path}", absRootMethods, async (HttpContext ctx, string? path, ILogger<Program> logger) =>
	{
		var referer = ctx.Request.Headers.Referer.ToString();
		var fluidd = config.GetValue<string>("PrinterUI:FluiddUrl");
		var mainsail = config.GetValue<string>("PrinterUI:MainsailUrl");
		string? target = null;
		if (!string.IsNullOrWhiteSpace(referer) && referer.Contains("/mainsail", StringComparison.OrdinalIgnoreCase)) target = mainsail;
		else if (!string.IsNullOrWhiteSpace(referer) && referer.Contains("/fluidd", StringComparison.OrdinalIgnoreCase)) target = fluidd;
		if (string.IsNullOrWhiteSpace(target)) target = fluidd ?? mainsail;
		if (string.IsNullOrWhiteSpace(target)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("UI target not configured"); return; }
		logger.LogDebug("ABS-ROOT /img/{Path} via referer='{Referer}' -> {Target}", path ?? string.Empty, referer, target);
		await ProxyUtil.ProxyRequest(ctx, target, "img/" + (path ?? string.Empty));
	});
	app.MapMethods("/manifest.webmanifest", absRootMethods, async (HttpContext ctx, ILogger<Program> logger) =>
	{
		var referer = ctx.Request.Headers.Referer.ToString();
		var fluidd = config.GetValue<string>("PrinterUI:FluiddUrl");
		var mainsail = config.GetValue<string>("PrinterUI:MainsailUrl");
		string? target = null;
		if (!string.IsNullOrWhiteSpace(referer) && referer.Contains("/mainsail", StringComparison.OrdinalIgnoreCase)) target = mainsail;
		else if (!string.IsNullOrWhiteSpace(referer) && referer.Contains("/fluidd", StringComparison.OrdinalIgnoreCase)) target = fluidd;
		if (string.IsNullOrWhiteSpace(target)) target = fluidd ?? mainsail;
		if (string.IsNullOrWhiteSpace(target)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("UI target not configured"); return; }
		logger.LogDebug("ABS-ROOT /manifest.webmanifest via referer='{Referer}' -> {Target}", referer, target);
		await ProxyUtil.ProxyRequest(ctx, target, "manifest.webmanifest");
	});
	app.MapMethods("/sw.js", absRootMethods, async (HttpContext ctx, ILogger<Program> logger) =>
	{
		var referer = ctx.Request.Headers.Referer.ToString();
		var fluidd = config.GetValue<string>("PrinterUI:FluiddUrl");
		var mainsail = config.GetValue<string>("PrinterUI:MainsailUrl");
		string? target = null;
		if (!string.IsNullOrWhiteSpace(referer) && referer.Contains("/mainsail", StringComparison.OrdinalIgnoreCase)) target = mainsail;
		else if (!string.IsNullOrWhiteSpace(referer) && referer.Contains("/fluidd", StringComparison.OrdinalIgnoreCase)) target = fluidd;
		if (string.IsNullOrWhiteSpace(target)) target = fluidd ?? mainsail;
		if (string.IsNullOrWhiteSpace(target)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("UI target not configured"); return; }
		logger.LogDebug("ABS-ROOT /sw.js via referer='{Referer}' -> {Target}", referer, target);
		await ProxyUtil.ProxyRequest(ctx, target, "sw.js");
	});

	// Support absolute-root asset paths emitted by the apps (e.g., /mainsail/assets/...)
	app.MapMethods("/mainsail/{**path}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }, async (HttpContext ctx, string? path, ILogger<Program> logger) =>
	{
		var target = config.GetValue<string>("PrinterUI:MainsailUrl");
		logger.LogDebug("{Method} /mainsail/{Path} -> target={Target}", ctx.Request.Method, path ?? "", target ?? "NOT CONFIGURED");
		if (string.IsNullOrWhiteSpace(target)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Mainsail URL not configured"); return; }
		await ProxyUtil.ProxyRequest(ctx, target, path ?? "");
	});

	app.MapMethods("/fluidd/{**path}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }, async (HttpContext ctx, string? path) =>
	{
		var target = config.GetValue<string>("PrinterUI:FluiddUrl");
		if (string.IsNullOrWhiteSpace(target)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Fluidd URL not configured"); return; }
		await ProxyUtil.ProxyRequest(ctx, target, path ?? "");
	});

	// Same-origin Moonraker HTTP proxy endpoints + WebSocket tunnel
	string? moonrakerBase = config.GetValue<string>("Moonraker:BaseUrl");
	if (!string.IsNullOrWhiteSpace(moonrakerBase))
	{
		var httpMethods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

		// Also support relative API calls when the UI is served under a prefix (e.g., /fluidd/printer/...)
		// These must target Moonraker, not the UI host.
		app.MapMethods("/fluidd/printer/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "printer/" + (path ?? string.Empty));
		});
		app.MapMethods("/fluidd/api/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "api/" + (path ?? string.Empty));
		});
		app.MapMethods("/fluidd/server/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "server/" + (path ?? string.Empty));
		});
		app.MapMethods("/fluidd/machine/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "machine/" + (path ?? string.Empty));
		});
		app.MapMethods("/fluidd/access/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "access/" + (path ?? string.Empty));
		});

		app.MapMethods("/mainsail/printer/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "printer/" + (path ?? string.Empty));
		});
		app.MapMethods("/mainsail/api/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "api/" + (path ?? string.Empty));
		});
		app.MapMethods("/mainsail/server/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "server/" + (path ?? string.Empty));
		});
		app.MapMethods("/mainsail/machine/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "machine/" + (path ?? string.Empty));
		});
		app.MapMethods("/mainsail/access/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "access/" + (path ?? string.Empty));
		});
		app.MapMethods("/printer/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "printer/" + (path ?? string.Empty));
		});
		app.MapMethods("/api/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "api/" + (path ?? string.Empty));
		});
		app.MapMethods("/server/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "server/" + (path ?? string.Empty));
		});
		app.MapMethods("/machine/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "machine/" + (path ?? string.Empty));
		});
		app.MapMethods("/access/{**path}", httpMethods, async (HttpContext ctx, string? path) =>
		{
			await ProxyUtil.ProxyRequest(ctx, moonrakerBase!, "access/" + (path ?? string.Empty));
		});

		// WebSocket tunnel for Moonraker at /websocket
		app.Map("/websocket", async (HttpContext ctx, ILogger<Program> logger) =>
		{
			if (!ctx.WebSockets.IsWebSocketRequest)
			{
				ctx.Response.StatusCode = 400;
				await ctx.Response.WriteAsync("Expected WebSocket request");
				return;
			}

			// Build upstream ws(s):// URL from configured Moonraker:BaseUrl and preserve query (e.g., token=...)
			var ub = new UriBuilder(moonrakerBase!);
			if (string.Equals(ub.Scheme, "http", StringComparison.OrdinalIgnoreCase)) ub.Scheme = "ws";
			else if (string.Equals(ub.Scheme, "https", StringComparison.OrdinalIgnoreCase)) ub.Scheme = "wss";
			ub.Path = ub.Path.TrimEnd('/') + "/websocket";
			// Carry through query string (tokens etc.)
			var qs = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : null;
			if (!string.IsNullOrEmpty(qs)) ub.Query = qs!.TrimStart('?');
			var upstreamUri = ub.Uri;

			// Attempt upstream WebSocket connect with a few retries. Create a fresh ClientWebSocket
			// for each attempt because a failed ConnectAsync leaves it unusable.
			System.Net.WebSockets.ClientWebSocket? upstream = null;
			Exception? lastConnectEx = null;
			var maxAttempts = 3;
			for (int attempt = 1; attempt <= maxAttempts; attempt++)
			{
				upstream = new System.Net.WebSockets.ClientWebSocket();
				upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

				// Only forward a minimal, known-safe set of headers for the WS handshake.
				var allowed = new[] { "Origin", "Cookie", "Authorization" };
				try
				{
					var hdrNames = string.Join(",", ctx.Request.Headers.Select(h => h.Key).Where(k => allowed.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray());
					logger.LogDebug("Forwarding request headers to upstream (names): {HeaderNames}", hdrNames);
				}
				catch { }

				foreach (var name in allowed)
				{
					if (ctx.Request.Headers.TryGetValue(name, out var vals))
					{
						try { upstream.Options.SetRequestHeader(name, vals.ToString()); } catch { }
					}
				}

				// If no Origin header present from client, set a reasonable default so upstream origin checks pass
				if (!ctx.Request.Headers.ContainsKey("Origin"))
				{
					try
					{
						var defaultOrigin = ctx.Request.Scheme + "://" + ctx.Request.Host.Value;
						upstream.Options.SetRequestHeader("Origin", defaultOrigin);
						logger.LogDebug("Set default Origin header for upstream: {Origin}", defaultOrigin);
					}
					catch { }
				}

				// Add subprotocols properly (if any requested by client)
				if (ctx.Request.Headers.TryGetValue("Sec-WebSocket-Protocol", out var protocols))
				{
					foreach (var proto in protocols)
					{
						if (proto == null) continue;
						foreach (var p in proto.Split(',', StringSplitOptions.RemoveEmptyEntries))
						{
							var trimmed = p.Trim();
							if (!string.IsNullOrEmpty(trimmed))
							{
								try { upstream.Options.AddSubProtocol(trimmed); } catch { }
							}
						}
					}
				}

				logger.LogDebug("Connecting upstream attempt {Attempt}/{MaxAttempts} {UpstreamUri} (Origin={Origin})", attempt, maxAttempts, upstreamUri, ctx.Request.Headers["Origin"]);
				try
				{
					using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
					connectCts.CancelAfter(TimeSpan.FromSeconds(10));
					await upstream.ConnectAsync(upstreamUri, connectCts.Token);
					lastConnectEx = null;
					break; // connected
				}
				catch (OperationCanceledException oce)
				{
					if (ctx.RequestAborted.IsCancellationRequested)
					{
						logger.LogDebug("Client aborted websocket request while connecting to upstream {UpstreamUri}", upstreamUri);
						try { upstream.Dispose(); } catch { }
						return;
					}
					lastConnectEx = oce;
					logger.LogWarning(oce, "Upstream connect attempt {Attempt} timed out for {UpstreamUri}", attempt, upstreamUri);
					try { upstream.Dispose(); } catch { }
					upstream = null;
					if (attempt < maxAttempts)
					{
						await Task.Delay(TimeSpan.FromMilliseconds(500));
						continue;
					}
				}
				catch (Exception ex)
				{
					lastConnectEx = ex;
					logger.LogWarning(ex, "Upstream connect attempt {Attempt} failed for {UpstreamUri}", attempt, upstreamUri);
					try { upstream.Dispose(); } catch { }
					upstream = null;
					if (attempt < maxAttempts)
					{
						await Task.Delay(TimeSpan.FromMilliseconds(500));
						continue;
					}
				}
			}

			// If upstream failed to connect, accept downstream and send a JSON-RPC style error then close
			if (upstream == null)
			{
				if (lastConnectEx is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested)
				{
					logger.LogDebug("Aborted upstream websocket connect after downstream cancellation: {UpstreamUri}", upstreamUri);
					return;
				}

				logger.LogWarning(lastConnectEx, "Upstream connect failed after {Attempts} attempts: {UpstreamUri}", maxAttempts, upstreamUri);

				using var downstreamErr = await ctx.WebSockets.AcceptWebSocketAsync();
				try
				{
					var errorMsg = System.Text.Json.JsonSerializer.Serialize(new
					{
						jsonrpc = "2.0",
						error = new { code = -32000, message = $"Moonraker connection failed: {lastConnectEx?.Message ?? "Unknown error"}" }
					});
					var errorBytes = System.Text.Encoding.UTF8.GetBytes(errorMsg);
					await downstreamErr.SendAsync(new ArraySegment<byte>(errorBytes), System.Net.WebSockets.WebSocketMessageType.Text, true, ctx.RequestAborted);
				}
				catch { }
				try { await downstreamErr.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.InternalServerError, "Upstream connection failed", CancellationToken.None); } catch { }
				return;
			}

			// Upstream connected — accept downstream now and use the upstream-chosen subprotocol (if any)
			logger.LogInformation("Upstream WebSocket connected. Upstream chosen subprotocol: {SubProtocol}", upstream.SubProtocol ?? "<none>");
			using var downstream = await ctx.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
			logger.LogDebug("Upstream connected, starting bidirectional tunnel (subprotocol={SubProtocol})", upstream.SubProtocol);

			var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
			var pump1 = ProxyUtil.PumpWebSocket(downstream, upstream, cts.Token);
			var pump2 = ProxyUtil.PumpWebSocket(upstream, downstream, cts.Token);
			await Task.WhenAny(pump1, pump2);
			cts.Cancel();
			logger.LogDebug("Tunnel closed");
		});
	}

	// Config API endpoints (lightweight state)
	app.MapGet("/api/config/state", (HttpContext ctx) =>
	{
		var autoBroadcastEnabled = config.GetValue<bool>("YouTube:LiveBroadcast:Enabled");
		var autoUploadEnabled = config.GetValue<bool>("YouTube:TimelapseUpload:Enabled");
		var endStreamAfterPrintEnabled = config.GetValue<bool?>("YouTube:LiveBroadcast:EndStreamAfterPrint") ?? false;
		// Audio feature flag controls whether /api/audio/stream serves audio and is used by internal streamers
		var audioEnabled = config.GetValue<bool?>("Audio:Enabled") ?? true;
		return Results.Json(new { autoBroadcastEnabled, autoUploadEnabled, endStreamAfterPrintEnabled, audioEnabled });
	});

	app.MapPost("/api/config/auto-broadcast", (HttpContext ctx, ILogger<Program> logger) =>
	{
		var raw = ctx.Request.Query["enabled"].ToString();
		bool enabled;
		if (!bool.TryParse(raw, out enabled))
		{
			// Accept "1"/"0" as well for robustness
			enabled = raw == "1";
		}
		config["YouTube:LiveBroadcast:Enabled"] = enabled.ToString();
		logger.LogInformation("Auto-broadcast: {State}", enabled ? "enabled" : "disabled");
		return Results.Json(new { success = true, enabled });
	});

	app.MapPost("/api/config/auto-upload", (HttpContext ctx, ILogger<Program> logger) =>
	{
		var raw = ctx.Request.Query["enabled"].ToString();
		bool enabled;
		if (!bool.TryParse(raw, out enabled))
		{
			enabled = raw == "1";
		}
		config["YouTube:TimelapseUpload:Enabled"] = enabled.ToString();
		logger.LogInformation("Auto-upload timelapses: {State}", enabled ? "enabled" : "disabled");
		return Results.Json(new { success = true, enabled });
	});

	app.MapPost("/api/config/end-stream-after-print", (HttpContext ctx, ILogger<Program> logger) =>
	{
		var raw = ctx.Request.Query["enabled"].ToString();
		bool enabled;
		if (!bool.TryParse(raw, out enabled))
		{
			enabled = raw == "1";
		}
		config["YouTube:LiveBroadcast:EndStreamAfterPrint"] = enabled.ToString();
		logger.LogInformation("End stream after print: {State}", enabled ? "enabled" : "disabled");
		return Results.Json(new { success = true, enabled });
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
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			var (ok, message, broadcastId) = await orchestrator.StartBroadcastAsync(ctx.RequestAborted);
			if (ok) return Results.Json(new { success = true, broadcastId });
			return Results.Json(new { success = false, error = message });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Manual force Go Live if YouTube Studio shows data but it's stuck in preview/testing
	app.MapPost("/api/live/force-go-live", async (HttpContext ctx) =>
	{
		try
		{
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			if (!orchestrator.IsBroadcastActive || string.IsNullOrWhiteSpace(orchestrator.CurrentBroadcastId))
			{
				return Results.Json(new { success = false, error = "No active broadcast" });
			}
			var bid = orchestrator.CurrentBroadcastId!;
			var yt = ctx.RequestServices.GetRequiredService<YouTubeControlService>();
			logger.LogInformation("HTTP /api/live/force-go-live request received");
			if (!await yt.AuthenticateAsync(ctx.RequestAborted))
			{
				return Results.Json(new { success = false, error = "YouTube authentication failed" });
			}
			var ok = await yt.TransitionBroadcastToLiveWhenReadyAsync(bid, TimeSpan.FromSeconds(180), 12, ctx.RequestAborted);
			return Results.Json(new { success = ok });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Dump diagnostics for the current broadcast/stream (helps when stuck in preview)
	app.MapGet("/api/live/debug", async (HttpContext ctx) =>
	{
		try
		{
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			if (!orchestrator.IsBroadcastActive || string.IsNullOrWhiteSpace(orchestrator.CurrentBroadcastId))
			{
				return Results.Json(new { success = false, error = "No active broadcast" });
			}
			var bid = orchestrator.CurrentBroadcastId!;
			var yt = ctx.RequestServices.GetRequiredService<YouTubeControlService>();
			logger.LogInformation("HTTP /api/live/debug request received");
			if (!await yt.AuthenticateAsync(ctx.RequestAborted))
			{
				return Results.Json(new { success = false, error = "YouTube authentication failed" });
			}
			await yt.LogBroadcastAndStreamResourcesAsync(bid, null, ctx.RequestAborted);
			return Results.Json(new { success = true });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	app.MapGet("/api/live/status", async (HttpContext ctx) =>
	{
		try
		{
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			var isLive = orchestrator.IsBroadcastActive;
			var broadcastId = orchestrator.CurrentBroadcastId;
			var streamerRunning = orchestrator.IsStreaming;
			var waitingForIngestion = orchestrator.IsWaitingForIngestion;
			string? privacy = null;

			// Try to fetch current privacy status if broadcast is active
			if (isLive && !string.IsNullOrWhiteSpace(broadcastId))
			{
				try
				{
					var yt = ctx.RequestServices.GetRequiredService<YouTubeControlService>();
					logger.LogInformation("HTTP /api/live/status request received");
					if (await yt.AuthenticateAsync(ctx.RequestAborted))
					{
						privacy = await yt.GetBroadcastPrivacyAsync(broadcastId, ctx.RequestAborted);
					}
				}
				catch
				{
					// Silently ignore errors fetching privacy status
				}
			}

			return Results.Json(new { isLive, broadcastId, streamerRunning, waitingForIngestion, privacy });
		}
		catch (Exception ex)
		{
			return Results.Json(new { isLive = false, broadcastId = (string?)null, streamerRunning = false, waitingForIngestion = false, privacy = (string?)null, error = ex.Message });
		}
	});

	app.MapPost("/api/live/stop", async (HttpContext ctx) =>
	{
		try
		{
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			var (ok, message) = await orchestrator.StopBroadcastAsync(ctx.RequestAborted);
			if (ok) return Results.Json(new { success = true });
			return Results.Json(new { success = false, error = message });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Update broadcast privacy status
	app.MapPost("/api/live/privacy", async (HttpContext ctx) =>
	{
		try
		{
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			if (!orchestrator.IsBroadcastActive || string.IsNullOrWhiteSpace(orchestrator.CurrentBroadcastId))
			{
				return Results.Json(new { success = false, error = "No active broadcast" });
			}

			var body = await ctx.Request.ReadFromJsonAsync<PrivacyUpdateRequest>();
			if (body == null || string.IsNullOrWhiteSpace(body.Privacy))
			{
				return Results.Json(new { success = false, error = "Privacy status is required" });
			}

			var broadcastId = orchestrator.CurrentBroadcastId!;
			var yt = ctx.RequestServices.GetRequiredService<YouTubeControlService>();
			logger.LogInformation("HTTP /api/live/privacy request received: {Privacy}", body?.Privacy);
			if (!await yt.AuthenticateAsync(ctx.RequestAborted))
			{
				return Results.Json(new { success = false, error = "YouTube authentication failed" });
			}

			var ok = await yt.UpdateBroadcastPrivacyAsync(broadcastId, body!.Privacy!, ctx.RequestAborted);
			return Results.Json(new { success = ok });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	app.MapPost("/api/live/chat", async (HttpContext ctx) =>
	{
		try
		{
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			if (!orchestrator.IsBroadcastActive || string.IsNullOrWhiteSpace(orchestrator.CurrentBroadcastId))
			{
				return Results.Json(new { success = false, error = "No active broadcast" });
			}

			var chatRequest = await ctx.Request.ReadFromJsonAsync<ChatMessageRequest>();
			if (chatRequest == null || string.IsNullOrWhiteSpace(chatRequest.Message))
			{
				return Results.Json(new { success = false, error = "Message is required" });
			}

			var yt = ctx.RequestServices.GetRequiredService<YouTubeControlService>();
			var ok = await yt.SendChatMessageAsync(orchestrator.CurrentBroadcastId!, chatRequest.Message, ctx.RequestAborted);
			return Results.Json(new { success = ok });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Manual repair endpoint: ensure stream health and recover broadcast if possible
	app.MapPost("/api/live/repair", async (HttpContext ctx) =>
	{
		try
		{
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			var ok = await orchestrator.EnsureStreamingHealthyAsync(ctx.RequestAborted);
			return Results.Json(new { success = ok });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// End stream after current song finishes
	app.MapPost("/api/stream/end-after-song", (HttpContext ctx) =>
	{
		try
		{
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			var enabledStr = ctx.Request.Query["enabled"].ToString();
			var enabled = string.Equals(enabledStr, "true", StringComparison.OrdinalIgnoreCase) || enabledStr == "1";

			orchestrator.SetEndStreamAfterSong(enabled);
			return Results.Json(new { success = true, enabled });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Get end-after-song status
	app.MapGet("/api/stream/end-after-song", (HttpContext ctx) =>
	{
		try
		{
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			var enabled = orchestrator.IsEndStreamAfterSongEnabled;
			return Results.Json(new { enabled });
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

	app.MapPost("/api/timelapses/{name}/generate", async (string name, HttpContext ctx, ILogger<Program> logger) =>
	{
		try
		{
			var timelapseDir = Path.Combine(timelapseManager.TimelapseDirectory, name);
			if (!Directory.Exists(timelapseDir))
			{
				return Results.Json(new { success = false, error = "Timelapse not found" });
			}

			var frameFiles = Directory.GetFiles(timelapseDir, "frame_*.jpg").OrderBy(f => f).ToArray();
			if (frameFiles.Length == 0)
			{
				return Results.Json(new { success = false, error = "No frames found" });
			}

			// Generate video directly using ffmpeg
			var folderName = Path.GetFileName(timelapseDir);
			var videoPath = Path.Combine(timelapseDir, $"{folderName}.mp4");

			logger.LogInformation("Generating video from {FrameCount} frames: {VideoPath}", frameFiles.Length, videoPath);

			// Use ffmpeg to create video
			var arguments = $"-y -framerate 30 -start_number 0 -i \"{timelapseDir}/frame_%06d.jpg\" -vf \"tpad=stop_mode=clone:stop_duration=5\" -c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p -movflags +faststart \"{videoPath}\"";

			var psi = new ProcessStartInfo
			{
				FileName = "ffmpeg",
				Arguments = arguments,
				UseShellExecute = false,
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				CreateNoWindow = true
			};

			using var proc = Process.Start(psi);
			if (proc == null)
			{
				return Results.Json(new { success = false, error = "Failed to start ffmpeg" });
			}

			var output = await proc.StandardError.ReadToEndAsync(ctx.RequestAborted);
			await proc.WaitForExitAsync(ctx.RequestAborted);

			if (proc.ExitCode == 0 && File.Exists(videoPath))
			{
				logger.LogInformation("Video created successfully: {VideoPath}", videoPath);
				return Results.Json(new { success = true, videoPath });
			}
			else
			{
				logger.LogError("ffmpeg failed with exit code {ExitCode}", proc.ExitCode);
				logger.LogError("ffmpeg output: {Output}", output);
				return Results.Json(new { success = false, error = $"ffmpeg failed with exit code {proc.ExitCode}" });
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Error generating video: {Message}", ex.Message);
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	app.MapPost("/api/timelapses/{name}/upload", async (string name, HttpContext ctx, ILogger<Program> logger) =>
	{
		try
		{
			var timelapseDir = Path.Combine(timelapseManager.TimelapseDirectory, name);
			if (!Directory.Exists(timelapseDir))
			{
				return Results.Json(new { success = false, error = "Timelapse not found" });
			}

			var videoFiles = Directory.GetFiles(timelapseDir, "*.mp4");
			if (videoFiles.Length == 0)
			{
				return Results.Json(new { success = false, error = "No video file found" });
			}

			var videoPath = videoFiles[0]; // Use first mp4 found

				    // Resolve the YouTubeControlService from DI and use it for the upload
				    // Note: we don't create/dispose a new instance here because the service is registered as a singleton
					var ytService = ctx.RequestServices.GetRequiredService<YouTubeControlService>();
					logger.LogInformation("HTTP /api/timelapses/{Name}/upload request received", name);
					logger.LogDebug("Timelapse dir: {TimelapseDir}; video: {VideoPath}", timelapseDir, videoPath);
			if (!await ytService.AuthenticateAsync(ctx.RequestAborted))
			{
				return Results.Json(new { success = false, error = "YouTube authentication failed" });
			}

			// Bypass upload config for manual UI uploads
			logger.LogInformation("Starting YouTube upload for timelapse {Name}", name);
			var videoId = await ytService.UploadTimelapseVideoAsync(videoPath, name, ctx.RequestAborted, true);
			logger.LogInformation("YouTube upload result videoId={VideoId}", videoId);

			if (!string.IsNullOrEmpty(videoId))
			{
				var url = $"https://www.youtube.com/watch?v={videoId}";

				// Save YouTube URL to metadata
				try
				{
					var metadataPath = Path.Combine(timelapseDir, ".metadata");
					var existingLines = File.Exists(metadataPath) ? File.ReadAllLines(metadataPath).ToList() : new List<string>();

					// Remove old YouTubeUrl if exists
					existingLines.RemoveAll(line => line.StartsWith("YouTubeUrl="));

					// Add new YouTubeUrl
					existingLines.Add($"YouTubeUrl={url}");

					File.WriteAllLines(metadataPath, existingLines);
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Failed to save YouTube URL to metadata: {Message}", ex.Message);
				}

				return Results.Json(new { success = true, videoId, url });
			}
			return Results.Json(new { success = false, error = "Upload failed" });
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Error uploading timelapse: {Message}", ex.Message);
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	app.MapDelete("/api/timelapses/{name}", (string name, HttpContext ctx) =>
	{
		try
		{
			var timelapseDir = Path.Combine(timelapseManager.TimelapseDirectory, name);
			if (!Directory.Exists(timelapseDir))
			{
				return Results.Json(new { success = false, error = "Timelapse not found" });
			}

			// Don't delete active timelapses
			if (timelapseManager.GetActiveSessionNames().Contains(name))
			{
				return Results.Json(new { success = false, error = "Cannot delete active timelapse" });
			}

			Directory.Delete(timelapseDir, true);
			return Results.Json(new { success = true });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Read timelapse metadata (small helper endpoint used by the UI to refresh a single card)
	app.MapGet("/api/timelapses/{name}/metadata", (string name) =>
	{
		try
		{
			var timelapseDir = Path.Combine(timelapseManager.TimelapseDirectory, name);
			if (!Directory.Exists(timelapseDir))
				return Results.Json(new { success = false, error = "Timelapse not found" });

			var metadataPath = Path.Combine(timelapseDir, ".metadata");
			if (!File.Exists(metadataPath))
			{
				// Try other common names
				var alt = new[] { Path.Combine(timelapseDir, "metadata"), Path.Combine(timelapseDir, ".metadata.txt"), Path.Combine(timelapseDir, ".meta") };
				metadataPath = alt.FirstOrDefault(File.Exists) ?? metadataPath;
			}

			if (!File.Exists(metadataPath))
				return Results.Json(new { success = true, youtubeUrl = (string?)null, createdAt = (string?)null });

			string? youtubeUrl = null;
			DateTime? createdAt = null;
			var lines = File.ReadAllLines(metadataPath);
			foreach (var line in lines)
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				var trimmed = line.Trim();
				var eqIdx = trimmed.IndexOf('=');
				var colonIdx = trimmed.IndexOf(':');
				int sep = -1;
				if (eqIdx >= 0) sep = eqIdx;
				else if (colonIdx >= 0) sep = colonIdx;

				if (sep >= 0)
				{
					var key = trimmed.Substring(0, sep).Trim();
					var val = trimmed.Substring(sep + 1).Trim();
					if (key.Equals("CreatedAt", StringComparison.OrdinalIgnoreCase))
					{
						if (DateTime.TryParse(val, out var dt)) createdAt = dt;
					}
					else if (key.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						youtubeUrl = val;
					}
				}
				else
				{
					if (Uri.IsWellFormedUriString(trimmed, UriKind.Absolute) && trimmed.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0)
						youtubeUrl = trimmed;
				}
			}

			return Results.Json(new { success = true, youtubeUrl, createdAt = createdAt?.ToString("O") });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Enhanced test page with timelapse management
	// Blazor pages are now served via MapRazorComponents below

	// Map API controllers (printer control API, etc.)
	app.MapControllers();

	app.MapRazorComponents<PrintStreamer.App>()
		.AddInteractiveServerRenderMode();

	// Get current configuration
	app.MapGet("/api/config", (HttpContext ctx) =>
	{
		try
		{
			var currentConfig = new
			{
				Stream = new
				{
					Source = config.GetValue<string>("Stream:Source"),
					TargetFps = config.GetValue<int?>("Stream:TargetFps") ?? 6,
					BitrateKbps = config.GetValue<int?>("Stream:BitrateKbps") ?? 800,
					Local = new
					{
						Enabled = config.GetValue<bool?>("Stream:Local:Enabled") ?? false
					},
					Audio = new
					{
						UseApiStream = config.GetValue<bool?>("Stream:Audio:UseApiStream") ?? true,
						Url = config.GetValue<string>("Stream:Audio:Url") ?? "http://127.0.0.1:8080/api/audio/stream"
					}
				},
				Audio = new
				{
					Folder = config.GetValue<string>("Audio:Folder") ?? "audio",
					Enabled = config.GetValue<bool?>("Audio:Enabled") ?? true
				},
				Moonraker = new
				{
					BaseUrl = config.GetValue<string>("Moonraker:BaseUrl"),
					ApiKey = config.GetValue<string>("Moonraker:ApiKey"),
					AuthHeader = config.GetValue<string>("Moonraker:AuthHeader") ?? "X-Api-Key"
				},
				Overlay = new
				{
					Enabled = config.GetValue<bool?>("Overlay:Enabled") ?? false,
					RefreshMs = config.GetValue<int?>("Overlay:RefreshMs") ?? 500,
					Template = config.GetValue<string>("Overlay:Template"),
					FontFile = config.GetValue<string>("Overlay:FontFile") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
					FontSize = config.GetValue<int?>("Overlay:FontSize") ?? 16,
					FontColor = config.GetValue<string>("Overlay:FontColor") ?? "white",
					Box = config.GetValue<bool?>("Overlay:Box") ?? true,
					BoxColor = config.GetValue<string>("Overlay:BoxColor") ?? "black@0.4",
					BoxBorderW = config.GetValue<int?>("Overlay:BoxBorderW") ?? 2,
					X = config.GetValue<string>("Overlay:X") ?? "0",
					Y = config.GetValue<string>("Overlay:Y") ?? "40",
					BannerFraction = config.GetValue<double?>("Overlay:BannerFraction") ?? 0.2,
					ShowFilamentInOverlay = config.GetValue<bool?>("Overlay:ShowFilamentInOverlay") ?? true,
					FilamentCacheSeconds = config.GetValue<int?>("Overlay:FilamentCacheSeconds") ?? 60
				},
				YouTube = new
				{
					OAuth = new
					{
						ClientId = config.GetValue<string>("YouTube:OAuth:ClientId"),
						ClientSecret = config.GetValue<string>("YouTube:OAuth:ClientSecret")
					},
					LiveBroadcast = new
					{
						Title = config.GetValue<string>("YouTube:LiveBroadcast:Title") ?? "3D Printer Live Stream",
						Description = config.GetValue<string>("YouTube:LiveBroadcast:Description") ?? "Live stream from my 3D printer.",
						Privacy = config.GetValue<string>("YouTube:LiveBroadcast:Privacy") ?? "unlisted",
						CategoryId = config.GetValue<string>("YouTube:LiveBroadcast:CategoryId") ?? "28",
						Enabled = config.GetValue<bool?>("YouTube:LiveBroadcast:Enabled") ?? true
					},
					Playlist = new
					{
						Name = config.GetValue<string>("YouTube:Playlist:Name") ?? "PrintStreamer",
						Privacy = config.GetValue<string>("YouTube:Playlist:Privacy") ?? "unlisted"
					},
					TimelapseUpload = new
					{
						Enabled = config.GetValue<bool?>("YouTube:TimelapseUpload:Enabled") ?? false,
						Privacy = config.GetValue<string>("YouTube:TimelapseUpload:Privacy") ?? "public",
						CategoryId = config.GetValue<string>("YouTube:TimelapseUpload:CategoryId") ?? "28"
					}
				},
				Timelapse = new
				{
					MainFolder = config.GetValue<string>("Timelapse:MainFolder") ?? "timelapse",
					Period = config.GetValue<string>("Timelapse:Period") ?? "00:01:00",
					LastLayerOffset = config.GetValue<int?>("Timelapse:LastLayerOffset") ?? 1
				},
				Serve = new
				{
					Enabled = config.GetValue<bool?>("Serve:Enabled") ?? true
				},
				PrinterUI = new
				{
					MainsailUrl = config.GetValue<string>("PrinterUI:MainsailUrl") ?? "http://192.168.1.117/mainsail",
					FluiddUrl = config.GetValue<string>("PrinterUI:FluiddUrl") ?? "http://192.168.1.117/fluid"
				}
			};
			return Results.Json(currentConfig);
		}
		catch (Exception ex)
		{
			return Results.Json(new { error = ex.Message });
		}
	});

	// Lightweight upstream health check for PrinterUI and Moonraker
	app.MapGet("/api/health/upstream", async (HttpContext ctx) =>
	{
		var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
		var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
		var mainsail = cfg.GetValue<string>("PrinterUI:MainsailUrl");
		var fluidd = cfg.GetValue<string>("PrinterUI:FluiddUrl");
		var moon = cfg.GetValue<string>("Moonraker:BaseUrl");
		var results = new Dictionary<string, object?>();

		// Helper to probe HTTP endpoint
		async Task<object> ProbeHttp(string? url)
		{
			if (string.IsNullOrWhiteSpace(url)) return "not-configured";
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, url);
				using var resp = await ProxyUtil.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
				return new { status = (int)resp.StatusCode, reason = resp.ReasonPhrase };
			}
			catch (OperationCanceledException) { return "timeout/canceled"; }
			catch (Exception ex) { return ex.Message; }
		}

		results["mainsail"] = await ProbeHttp(mainsail);
		results["fluidd"] = await ProbeHttp(fluidd);
		results["moonraker_http"] = await ProbeHttp(moon);

		// TCP probe for Moonraker host:port if available
		try
		{
			if (!string.IsNullOrWhiteSpace(moon))
			{
				var ub = new UriBuilder(moon);
				var host = ub.Host;
				var port = ub.Port > 0 ? ub.Port : (ub.Scheme == "https" ? 443 : 80);
				using var tcp = new System.Net.Sockets.TcpClient();
				var connectTask = tcp.ConnectAsync(host, port);
				var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(3), ctx.RequestAborted));
				results["moonraker_tcp"] = completed == connectTask && tcp.Connected ? "open" : "closed/timeout";
			}
			else results["moonraker_tcp"] = "not-configured";
		}
		catch (Exception ex)
		{
			results["moonraker_tcp"] = ex.Message;
		}

		return Results.Json(results);
	});

	// Save configuration
	app.MapPost("/api/config", async (HttpContext ctx, ILogger<Program> logger) =>
	{
		try
		{
			using var reader = new StreamReader(ctx.Request.Body);
			var body = await reader.ReadToEndAsync();

			if (string.IsNullOrWhiteSpace(body))
			{
				return Results.Json(new { success = false, error = "Invalid configuration data" });
			}

			// Validate JSON
			try
			{
				System.Text.Json.JsonDocument.Parse(body);
			}
			catch (System.Text.Json.JsonException)
			{
				return Results.Json(new { success = false, error = "Invalid JSON format" });
			}

			// Get the custom config file path from configuration
			var customConfigFile = config.GetValue<string>("CustomConfigFile") ?? "appsettings.Local.json";
			var configPath = Path.Combine(Directory.GetCurrentDirectory(), customConfigFile);

			logger.LogInformation("Saving configuration to: {ConfigFile}", customConfigFile);

			// Parse and re-serialize with indentation for pretty formatting
			var jsonDoc = System.Text.Json.JsonDocument.Parse(body);
			var options = new System.Text.Json.JsonSerializerOptions
			{
				WriteIndented = true,
				PropertyNamingPolicy = null
			};

			var jsonString = System.Text.Json.JsonSerializer.Serialize(jsonDoc.RootElement, options);
			await File.WriteAllTextAsync(configPath, jsonString);

			logger.LogInformation("Configuration saved to {ConfigFile}", customConfigFile);
			return Results.Json(new { success = true, message = "Configuration saved. Restart required for changes to take effect." });
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Error saving configuration: {Message}", ex.Message);
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Reset configuration to defaults
	app.MapPost("/api/config/reset", async (HttpContext ctx, ILogger<Program> logger) =>
	{
		try
		{
			var defaultConfig = new
			{
				Stream = new
				{
					Source = "http://192.168.1.2/webcam",
					Audio = new
					{
						UseApiStream = true,
						Url = "http://127.0.0.1:8080/api/audio/stream"
					}
				},
				Audio = new
				{
					Folder = "audio",
					Enabled = true
				},
				Moonraker = new
				{
					BaseUrl = "http://192.168.1.2:7125/",
					ApiKey = "",
					AuthHeader = "X-Api-Key"
				},
				Overlay = new
				{
					Enabled = false,
					RefreshMs = 500,
					Template = "Nozzle: {nozzle:0}°C/{nozzleTarget:0}°C | Bed: {bed:0}°C/{bedTarget:0}°C | Layer {layers} | {progress:0}%\nSpeed:{speed}mm/s | Flow:{flow} | Fil:{filament}m | ETA:{eta:hh:mm tt}",
					FontFile = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
					FontSize = 16,
					FontColor = "white",
					Box = true,
					BoxColor = "black@0.4",
					BoxBorderW = 8,
					X = "(w-tw)-20",
					Y = "",
					BannerFraction = 0.2,
					ShowFilamentInOverlay = true,
					FilamentCacheSeconds = 60
				},
				YouTube = new
				{
					OAuth = new
					{
						ClientId = "",
						ClientSecret = ""
					},
					LiveBroadcast = new
					{
						Title = "3D Printer Live Stream",
						Description = "Live stream from my 3D printer.",
						Privacy = "unlisted",
						CategoryId = "28",
						Enabled = true
					},
					Playlist = new
					{
						Name = "PrintStreamer",
						Privacy = "unlisted"
					}
				},
				Timelapse = new
				{
					MainFolder = "timelapse",
					Period = "00:01:00",
					Upload = true
				}
			};

			var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
			var options = new System.Text.Json.JsonSerializerOptions
			{
				WriteIndented = true,
				PropertyNamingPolicy = null
			};

			var jsonString = System.Text.Json.JsonSerializer.Serialize(defaultConfig, options);
			await File.WriteAllTextAsync(appSettingsPath, jsonString);

			logger.LogInformation("Configuration reset to defaults");
			return Results.Json(new { success = true, message = "Configuration reset to defaults" });
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Error resetting configuration: {Message}", ex.Message);
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Audio API
	app.MapGet("/api/audio/tracks", (HttpContext ctx) =>
	{
		var audio = ctx.RequestServices.GetRequiredService<AudioService>();
		var tracks = audio.Library.Select(t => new { t.Name }).ToArray();
		return Results.Json(tracks);
	});

	app.MapGet("/api/audio/state", (HttpContext ctx) =>
	{
		var audio = ctx.RequestServices.GetRequiredService<AudioService>();
		var st = audio.GetState();
		// Normalize enums to strings for UI consumption
		return Results.Json(new
		{
			IsPlaying = st.IsPlaying,
			Current = st.Current,
			Queue = st.Queue,
			Shuffle = st.Shuffle,
			Repeat = st.Repeat.ToString()
		});
	});

	app.MapPost("/api/audio/folder", (HttpContext ctx) =>
	{
		var path = ctx.Request.Query["path"].ToString();
		if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "Missing 'path'" });
		var audio = ctx.RequestServices.GetRequiredService<AudioService>();
		audio.SetFolder(path);
		return Results.Json(new { success = true, folder = audio.Folder });
	});

	app.MapPost("/api/audio/scan", (HttpContext ctx) =>
	{
		var audio = ctx.RequestServices.GetRequiredService<AudioService>();
		audio.Rescan();
		return Results.Json(new { success = true });
	});

	app.MapPost("/api/audio/queue", async (HttpContext ctx) =>
	{
		try
		{
			var names = new List<string>();
			// Accept names via JSON { names: ["track1", ...] } and/or query ?name=...&name=...
			using (var sr = new StreamReader(ctx.Request.Body))
			{
				var body = await sr.ReadToEndAsync();
				if (!string.IsNullOrWhiteSpace(body))
				{
					try
					{
						using var doc = System.Text.Json.JsonDocument.Parse(body);
						if (doc.RootElement.TryGetProperty("names", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
						{
							foreach (var el in arr.EnumerateArray())
							{
								if (el.ValueKind == System.Text.Json.JsonValueKind.String)
								{
									names.Add(el.GetString()!);
								}
							}
						}
					}
					catch { /* ignore parse errors */ }
				}
			}
			names.AddRange(ctx.Request.Query["name"].Where(s => !string.IsNullOrEmpty(s))!);

			var audio = ctx.RequestServices.GetRequiredService<AudioService>();
			audio.Enqueue(names.ToArray());
			return Results.Json(new { success = true, queued = names.Count });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});


	app.MapPost("/api/audio/clear", (HttpContext ctx) =>
	{
		var audio = ctx.RequestServices.GetRequiredService<AudioService>();
		audio.ClearQueue();
		return Results.Json(new { success = true });
	});

	// Remove specific named tracks from the queue
	app.MapPost("/api/audio/queue/remove", async (HttpContext ctx) =>
	{
		try
		{
			var names = new List<string>();
			using (var sr = new StreamReader(ctx.Request.Body))
			{
				var body = await sr.ReadToEndAsync();
				if (!string.IsNullOrWhiteSpace(body))
				{
					try
					{
						using var doc = System.Text.Json.JsonDocument.Parse(body);
						if (doc.RootElement.TryGetProperty("names", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
						{
							foreach (var el in arr.EnumerateArray())
							{
								if (el.ValueKind == System.Text.Json.JsonValueKind.String)
								{
									names.Add(el.GetString()!);
								}
							}
						}
					}
					catch { /* ignore parse errors */ }
				}
			}
			names.AddRange(ctx.Request.Query["name"].Where(s => !string.IsNullOrEmpty(s))!);

			if (names.Count == 0) return Results.BadRequest(new { error = "Missing 'name'" });

			var audio = ctx.RequestServices.GetRequiredService<AudioService>();
			var removed = audio.RemoveFromQueue(names.ToArray());
			return Results.Json(new { success = true, removed });
		}
		catch (Exception ex)
		{
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	app.MapPost("/api/audio/play", (HttpContext ctx) => { var a = ctx.RequestServices.GetRequiredService<AudioService>(); a.Play(); return Results.Json(new { success = true }); });
	app.MapPost("/api/audio/pause", (HttpContext ctx) => { var a = ctx.RequestServices.GetRequiredService<AudioService>(); a.Pause(); return Results.Json(new { success = true }); });
	app.MapPost("/api/audio/toggle", (HttpContext ctx) => { var a = ctx.RequestServices.GetRequiredService<AudioService>(); a.Toggle(); return Results.Json(new { success = true }); });
	app.MapPost("/api/audio/next", (HttpContext ctx) =>
	{
		var a = ctx.RequestServices.GetRequiredService<AudioService>();
		a.Next();
		try
		{
			var b = ctx.RequestServices.GetService<AudioBroadcastService>();
			b?.InterruptFfmpeg();
		}
		catch { }
		return Results.Json(new { success = true });
	});
	app.MapPost("/api/audio/prev", (HttpContext ctx) =>
	{
		var a = ctx.RequestServices.GetRequiredService<AudioService>();
		a.Prev();
		try
		{
			var b = ctx.RequestServices.GetService<AudioBroadcastService>();
			b?.InterruptFfmpeg();
		}
		catch { }
		return Results.Json(new { success = true });
	});
	app.MapPost("/api/audio/shuffle", (HttpContext ctx) => { var a = ctx.RequestServices.GetRequiredService<AudioService>(); var raw = ctx.Request.Query["enabled"].ToString(); bool enabled = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1"; a.SetShuffle(enabled); return Results.Json(new { success = true, enabled }); });
	app.MapPost("/api/audio/repeat", (HttpContext ctx) => { var a = ctx.RequestServices.GetRequiredService<AudioService>(); var m = ctx.Request.Query["mode"].ToString(); var mode = m?.ToLowerInvariant() switch { "one" => PrintStreamer.Services.RepeatMode.One, "all" => PrintStreamer.Services.RepeatMode.All, _ => PrintStreamer.Services.RepeatMode.None }; a.SetRepeat(mode); return Results.Json(new { success = true, mode = mode.ToString() }); });

	// Preview a specific audio file directly (browser-only). Streams the raw file with best-effort MIME type.
	app.MapGet("/api/audio/preview", (HttpContext ctx) =>
	{
		try
		{
			var name = ctx.Request.Query["name"].ToString();
			if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "Missing 'name'" });
			var audio = ctx.RequestServices.GetRequiredService<AudioService>();
			var path = audio.GetPathForName(name);
			if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return Results.NotFound();

			var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
			var contentType = ext switch
			{
				".mp3" => "audio/mpeg",
				".aac" => "audio/aac",
				".m4a" => "audio/mp4",
				".wav" => "audio/wav",
				".flac" => "audio/flac",
				".ogg" => "audio/ogg",
				".opus" => "audio/ogg",
				_ => "application/octet-stream"
			};
			return Results.File(path, contentType, enableRangeProcessing: true);
		}
		catch
		{
			return Results.NotFound();
		}
	});

	// Play a specific track immediately on the live stream
	app.MapPost("/api/audio/play-track", (HttpContext ctx) =>
	{
		var name = ctx.Request.Query["name"].ToString();
		if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "Missing 'name'" });
		var audio = ctx.RequestServices.GetRequiredService<AudioService>();
		if (!audio.TrySelectByName(name, out var selected))
		{
			return Results.Json(new { success = false, error = "Track not found" });
		}
		// Interrupt ffmpeg so the broadcast switches to the selected file
		try
		{
			var b = ctx.RequestServices.GetService<AudioBroadcastService>();
			b?.InterruptFfmpeg();
		}
		catch { }
		return Results.Json(new { success = true, track = System.IO.Path.GetFileName(selected!) });
	});

	// Live-only audio stream endpoint (MP3). Subscribes to a centralized broadcaster so
	// reconnects resume at the live edge instead of starting a new track.
	app.MapGet("/api/audio/stream", async (HttpContext ctx) =>
	{
		var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
		var enabled = cfg.GetValue<bool?>("Audio:Enabled") ?? true;
		if (!enabled)
		{
			var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
			await StreamSilentAudioAsync(ctx, logger, ctx.RequestAborted);
			return;
		}

		ctx.Response.StatusCode = 200;
		ctx.Response.Headers["Content-Type"] = "audio/mpeg";
		ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
		ctx.Response.Headers["Pragma"] = "no-cache";
		await ctx.Response.Body.FlushAsync();

		var broadcaster = ctx.RequestServices.GetRequiredService<AudioBroadcastService>();
		try
		{
			await foreach (var chunk in broadcaster.Stream(ctx.RequestAborted))
			{
				await ctx.Response.Body.WriteAsync(chunk, 0, chunk.Length, ctx.RequestAborted);
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
			logger.LogError(ex, "Client stream error");
		}
	});

	// Audio broadcast diagnostics
	app.MapGet("/api/audio/broadcast/status", (HttpContext ctx) =>
	{
		var b = ctx.RequestServices.GetRequiredService<AudioBroadcastService>();
		return Results.Json(b.GetStatus());
	});

	// Audio stream endpoint for data flow pipeline (/stream/audio)
	// Mirrors /api/audio/stream for consistent endpoint naming
	app.MapGet("/stream/audio", async (HttpContext ctx) =>
	{
		var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
		var enabled = cfg.GetValue<bool?>("Audio:Enabled") ?? true;
		if (!enabled)
		{
			var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
			await StreamSilentAudioAsync(ctx, logger, ctx.RequestAborted);
			return;
		}

		ctx.Response.StatusCode = 200;
		ctx.Response.Headers["Content-Type"] = "audio/mpeg";
		ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
		ctx.Response.Headers["Pragma"] = "no-cache";
		await ctx.Response.Body.FlushAsync();

		var broadcaster = ctx.RequestServices.GetRequiredService<AudioBroadcastService>();
		try
		{
			await foreach (var chunk in broadcaster.Stream(ctx.RequestAborted))
			{
				await ctx.Response.Body.WriteAsync(chunk, 0, chunk.Length, ctx.RequestAborted);
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
			logger.LogError(ex, "Client stream error");
		}
	});

	static async Task StreamSilentAudioAsync(HttpContext ctx, ILogger<Program> logger, CancellationToken ct)
	{
		ctx.Response.StatusCode = 200;
		ctx.Response.Headers["Content-Type"] = "audio/mpeg";
		ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
		ctx.Response.Headers["Pragma"] = "no-cache";
		await ctx.Response.Body.FlushAsync(ct);

		Process? proc = null;
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "ffmpeg",
				Arguments = "-hide_banner -loglevel error -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -c:a libmp3lame -b:a 128k -f mp3 -",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};

			proc = Process.Start(psi);
			if (proc == null)
			{
				ctx.Response.StatusCode = 500;
				await ctx.Response.WriteAsync("Silent audio unavailable", ct);
				return;
			}

			using var reg = ct.Register(() =>
			{
				try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
			});

			var buffer = new byte[16 * 1024];
			var stdout = proc.StandardOutput.BaseStream;
			while (!ct.IsCancellationRequested)
			{
				var read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
				if (read <= 0) break;
				await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			logger.LogError(ex, "Silent audio stream error");
		}
		finally
		{
			try { if (proc != null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
			try { proc?.Dispose(); } catch { }
		}
	}

	// YouTube polling manager diagnostics
	app.MapGet("/api/youtube/polling/status", (HttpContext ctx) =>
	{
		var pm = ctx.RequestServices.GetRequiredService<YouTubePollingManager>();
		var stats = pm.GetStats();
		return Results.Json(stats);
	});

	app.MapPost("/api/youtube/polling/clear-cache", (HttpContext ctx) =>
	{
		var pm = ctx.RequestServices.GetRequiredService<YouTubePollingManager>();
		pm.ClearCache();
		return Results.Json(new { success = true, message = "Cache cleared" });
	});

	app.Logger.LogInformation("Starting proxy server on http://0.0.0.0:8080/stream");

	// Handle graceful shutdown - this will run regardless of mode since the host is started below
	var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
	lifetime.ApplicationStopping.Register(() =>
	{
		var logger = app.Services.GetRequiredService<ILogger<Program>>();
		logger.LogInformation("Shutting down...");
		streamCts?.Cancel();
		timelapseManager?.Dispose();
	});

	// Start local stream AFTER the web server is listening (to avoid race condition)
	lifetime.ApplicationStarted.Register(() =>
	{
		if (config.GetValue<bool?>("Stream:Local:Enabled") ?? false)
		{
			var logger = app.Services.GetRequiredService<ILogger<Program>>();
			logger.LogInformation("Web server ready, starting local preview stream...");
			// Start a local stream on startup for preview
			// Ensure audio broadcaster is constructed so the API audio endpoint is available
			try
			{
				// Resolve the AudioBroadcastService (constructor will start its internal feed/supervisor)
				var _ = app.Services.GetRequiredService<AudioBroadcastService>();
			}
			catch { }

			// Small delay to give the audio feed a moment to start before ffmpeg connects
			Task.Delay(500).ContinueWith(async _ =>
			{
				try
				{
					var streamService = app.Services.GetRequiredService<StreamService>();
					if (!streamService.IsStreaming)
					{
						logger.LogInformation("Starting local preview stream");
						await streamService.StartStreamAsync(null, null, CancellationToken.None);
					}
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Failed to start local preview");
				}
			});
		}
	});

	// Serve Blazor component assets (CSS, JS)
	var componentsFolder = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Components"));
	if (Directory.Exists(componentsFolder))
	{
		app.UseStaticFiles(new StaticFileOptions
		{
			FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(componentsFolder),
			RequestPath = "/Components",
			ServeUnknownFileTypes = false
		});
	}

	// Debug endpoint for pipeline health check
	app.MapGet("/api/debug/pipeline", (HttpContext ctx) =>
	{
		var sources = new Dictionary<string, string>
		{
			["stage_1_source"] = "http://127.0.0.1:8080/stream/source",
			["stage_2_overlay"] = "http://127.0.0.1:8080/stream/overlay",
			["stage_3_audio"] = "http://127.0.0.1:8080/stream/audio",
			["stage_4_mix"] = "http://127.0.0.1:8080/stream/mix",
			["description"] = "Data flow pipeline endpoints (Stage 1→2→3→4→YouTube RTMP)"
		};
		return Results.Json(sources);
	});

}

// Start the host so IHostedService instances (MoonrakerHostedService) are started in all modes
await app.RunAsync();

// (ProxyUtil helper defined at top of file)

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
	Console.WriteLine("Architecture:");
	Console.WriteLine("  1. WebCam Proxy (/stream) - Proxies MJPEG webcam, handles camera simulation");
	Console.WriteLine("  2. Local Stream - ffmpeg reads /stream and outputs to RTMP");
	Console.WriteLine("  3. YouTube Live Broadcast - OAuth creates broadcast, configures RTMP output");
	Console.WriteLine();
	Console.WriteLine("Configuration Methods:");
	Console.WriteLine("  1. appsettings.json (base configuration)");
	Console.WriteLine("  2. Environment variables (use __ for nested keys, e.g., Stream__Source)");
	Console.WriteLine("  3. Command-line arguments (use --Key value or --Key=value)");
	Console.WriteLine();
	Console.WriteLine("Required Configuration:");
	Console.WriteLine("  Stream:Source                     - MJPEG webcam URL (required)");
	Console.WriteLine("  Stream:Local:Enabled              - Enable local stream preview (true/false)");
	Console.WriteLine();
	Console.WriteLine("YouTube Live Broadcast (Optional):");
	Console.WriteLine("  YouTube:OAuth:ClientId            - OAuth client ID for YouTube API");
	Console.WriteLine("  YouTube:OAuth:ClientSecret        - OAuth client secret for YouTube API");
	Console.WriteLine("  YouTube:LiveBroadcast:Enabled     - Enable automatic broadcast creation (true/false)");
	Console.WriteLine();
	Console.WriteLine("Note: YouTube live broadcasts require OAuth. Use /api/live/start endpoint to go live.");
	Console.WriteLine();
	Console.WriteLine("Examples:");
	Console.WriteLine();
	Console.WriteLine("  # Run with defaults from appsettings.json");
	Console.WriteLine("  dotnet run");
	Console.WriteLine();
	Console.WriteLine("  # Set source on command-line");
	Console.WriteLine("  dotnet run -- --Stream:Source \"http://printer/webcam/?action=stream\"");
	Console.WriteLine();
	Console.WriteLine("  # Use environment variables");
	Console.WriteLine("  export Stream__Source=\"http://printer/webcam/?action=stream\"");
	Console.WriteLine("  dotnet run");
	Console.WriteLine();
	Console.WriteLine("See README.md for complete documentation.");
	Console.WriteLine();
}


// Proxy utilities must be declared before top-level statements
internal static class ProxyUtil
{
	internal static ILogger? Logger;
	internal static readonly HttpClient Client;

	// Static ctor so we can configure handler timeouts separately from overall HttpClient timeout
	static ProxyUtil()
	{
		// Use a SocketsHttpHandler so we can set a short connect timeout but keep the overall
		// HttpClient timeout infinite (we rely on downstream cancellation tokens).
		var handler = new SocketsHttpHandler
		{
			// Fail fast when upstream is unreachable (reduced to 5s for better responsiveness)
			ConnectTimeout = TimeSpan.FromSeconds(5),
			// Allow connection pooling and keep-alive for better performance
			PooledConnectionLifetime = TimeSpan.FromMinutes(5),
			PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
		};
		Client = new HttpClient(handler)
		{
			// Let the request be controlled by the passed CancellationToken (ctx.RequestAborted)
			Timeout = System.Threading.Timeout.InfiniteTimeSpan
		};
	}

	internal static async Task ProxyRequest(HttpContext ctx, string targetBase, string path)
	{
		try
		{
			var targetUrl = targetBase.TrimEnd('/') + "/" + path.TrimStart('/');
			if (!string.IsNullOrWhiteSpace(ctx.Request.QueryString.Value))
			{
				targetUrl += ctx.Request.QueryString.Value;
			}

			// Log Moonraker API calls for debugging
			if (path.StartsWith("printer/") || path.StartsWith("api/") || path.StartsWith("server/") || path.StartsWith("machine/") || path.StartsWith("access/"))
			{
				Logger?.LogDebug("Moonraker Proxy {Method} {TargetUrl}", ctx.Request.Method, targetUrl);
			}
			else if (path.StartsWith("assets/") || path.Contains(".js") || path.Contains(".css"))
			{
				Logger?.LogDebug("Asset Proxy {Method} {Path} -> {TargetUrl}", ctx.Request.Method, path, targetUrl);
			}

			using var forward = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), targetUrl);

			// Copy headers (except Host, Transfer-Encoding)
			foreach (var header in ctx.Request.Headers)
			{
				var key = header.Key;
				if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
				if (!forward.Headers.TryAddWithoutValidation(key, header.Value.ToArray()))
				{
					// Some headers belong to content
					if (forward.Content == null) forward.Content = new StreamContent(ctx.Request.Body);
					try { forward.Content.Headers.TryAddWithoutValidation(key, header.Value.ToArray()); } catch { }
				}
			}

			// If request has a body and content wasn't set yet, set it
			if (forward.Content == null && (ctx.Request.ContentLength ?? 0) > 0)
			{
				forward.Content = new StreamContent(ctx.Request.Body);
			}

			var response = await Client.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

			// Copy response headers, removing frame-blocking ones
			ctx.Response.StatusCode = (int)response.StatusCode;

			// Log errors for debugging  
			if ((int)response.StatusCode >= 400)
			{
				Logger?.LogWarning("Proxy {StatusCode} from {TargetUrl}", response.StatusCode, targetUrl);
			}

			foreach (var header in response.Headers)
			{
				if (header.Key.Equals("X-Frame-Options", StringComparison.OrdinalIgnoreCase)) continue;
				if (header.Key.Equals("Content-Security-Policy", StringComparison.OrdinalIgnoreCase)) continue;
				if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
				if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue; // we'll manage body length
				try { ctx.Response.Headers[header.Key] = header.Value.ToArray(); } catch { }
			}
			foreach (var header in response.Content.Headers)
			{
				if (header.Key.Equals("Content-Security-Policy", StringComparison.OrdinalIgnoreCase)) continue;
				if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
				try { ctx.Response.Headers[header.Key] = header.Value.ToArray(); } catch { }
			}

			// Add CORS headers for Mainsail/Fluidd API calls
			ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
			ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, DELETE, OPTIONS";
			ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";

			// Handle HEAD and Not Modified without writing body
			if (string.Equals(ctx.Request.Method, "HEAD", StringComparison.OrdinalIgnoreCase) || (int)response.StatusCode == 304 || (int)response.StatusCode == 204)
			{
				// Nothing to write
			}
			else
			{
				var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
				var contentLength = response.Content.Headers.ContentLength ?? -1;

				// Decide whether to buffer the body for logging/inspection. Avoid buffering large or binary streams.
				bool likelyText = contentType.StartsWith("text/") || contentType.Contains("json") || contentType.Contains("javascript") || contentType.Contains("xml") || contentType.Contains("mpegurl");
				bool smallEnough = contentLength < 1024 * 1024 && contentLength != -1; // <1MB
				bool shouldBuffer = (path.StartsWith("access/") || path.StartsWith("api/") || (int)response.StatusCode >= 400 || likelyText) && (smallEnough || contentLength == -1 && likelyText);

				Logger?.LogDebug("Proxy {Path}: contentType={ContentType}, contentLength={ContentLength}, likelyText={LikelyText}, smallEnough={SmallEnough}, shouldBuffer={ShouldBuffer}",
					path, contentType, contentLength, likelyText, smallEnough, shouldBuffer);

				if (shouldBuffer)
				{
					var body = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
					// Log truncated body for diagnostics (redact tokens)
					var logBody = body?.Replace(Environment.NewLine, " ") ?? string.Empty;
					if (logBody.Length > 1000) logBody = logBody.Substring(0, 1000) + "...";
					Logger?.LogDebug("Proxy Response {StatusCode} ({ContentType};len={ContentLength}) from {TargetUrl}: {Body}", response.StatusCode, contentType, contentLength, targetUrl, logBody);

					// If HTML 200, inject WS shim and fix asset paths before writing
					if ((int)response.StatusCode == 200 && !string.IsNullOrWhiteSpace(contentType) && contentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						Logger?.LogDebug("Processing HTML for path: {Path}, contentType: {ContentType}", path, contentType);

						// Determine the proxy prefix (mainsail or fluidd) from the original request path
						string proxyPrefix = "";
						var requestPath = ctx.Request.Path.Value ?? "";
						if (requestPath.StartsWith("/proxy/mainsail", StringComparison.OrdinalIgnoreCase))
						{
							proxyPrefix = "/proxy/mainsail";
						}
						else if (requestPath.StartsWith("/proxy/fluidd", StringComparison.OrdinalIgnoreCase))
						{
							proxyPrefix = "/proxy/fluidd";
						}
						else if (requestPath.StartsWith("/mainsail", StringComparison.OrdinalIgnoreCase))
						{
							proxyPrefix = "/mainsail";
						}
						else if (requestPath.StartsWith("/fluidd", StringComparison.OrdinalIgnoreCase))
						{
							proxyPrefix = "/fluidd";
						}

						Logger?.LogDebug("Determined proxy prefix: '{ProxyPrefix}' from request path: {RequestPath}", proxyPrefix, requestPath);

						var safeBody = (body ?? string.Empty);

						// Check if this is a simple redirect page (no <head> tag)
						var isRedirectPage = !safeBody.Contains("<head", StringComparison.OrdinalIgnoreCase) &&
											 safeBody.Contains("window.location", StringComparison.OrdinalIgnoreCase);

						if (isRedirectPage && !string.IsNullOrEmpty(proxyPrefix))
						{
							// Rewrite relative redirects to use the proxy prefix
							Logger?.LogDebug("Detected redirect page, rewriting relative URLs for {Path}", path);
							safeBody = safeBody.Replace("'./fluidd'", "'/fluidd/'");
							safeBody = safeBody.Replace("'./mainsail'", "'/mainsail/'");
							safeBody = safeBody.Replace("\"./fluidd\"", "\"/fluidd/\"");
							safeBody = safeBody.Replace("\"./mainsail\"", "\"/mainsail/\"");
							safeBody = safeBody.Replace("href=\"./fluidd\"", "href=\"/fluidd/\"");
							safeBody = safeBody.Replace("href=\"./mainsail\"", "href=\"/mainsail/\"");

							var redirectBytes = System.Text.Encoding.UTF8.GetBytes(safeBody);
							await ctx.Response.Body.WriteAsync(redirectBytes, 0, redirectBytes.Length, ctx.RequestAborted);
						}
						else
						{
							// Build base tag separately and concatenate with the injection script
							var baseTag = string.IsNullOrEmpty(proxyPrefix) ? "" : $"<base href=\"{proxyPrefix}/\">";
							var injection = baseTag + @"<script>(function(){
try{
  console.log('[Proxy Shim] Loading Mainsail/Fluidd proxy shim...');
  
  // WebSocket proxy - intercept and rewrite to our proxy endpoint
  var WS=window.WebSocket;
  function rw(u){
    try{
      if(!u) return u;
      var x;
      try { x = new URL(u, window.location.href); } catch(e) { return u; }
      var s = x.search || '';
      // Use ws/wss when rewriting so the browser makes a proper WebSocket handshake
      var proto = (window.location.protocol === 'https:') ? 'wss:' : 'ws:';
      var host = window.location.host;
      var rewritten = proto + '//' + host + '/websocket' + s;
      console.log('[WS Shim] Rewriting WebSocket:', u, '->', rewritten);
      return rewritten;
    }catch(e){
      console.error('[WS Shim] Error rewriting:', e);
      return u;
    }
  }
  window.WebSocket=function(u,p){
    var nu=rw(u);
    try{return p?new WS(nu,p):new WS(nu);}catch(e){return new WS(nu);}
  };
  window.WebSocket.prototype=WS.prototype;
  
  console.log('[Proxy Shim] WebSocket proxy active');
}catch(e){console.error('[Proxy Shim] Critical error:',e);}
})();</script>";
							string result;
							// Remove any meta Content-Security-Policy tags so our injected inline script can run
							try
							{
								safeBody = System.Text.RegularExpressions.Regex.Replace(safeBody, "<meta[^>]*http-equiv\\s*=\\s*['\"]?Content-Security-Policy['\"]?[^>]*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
								safeBody = System.Text.RegularExpressions.Regex.Replace(safeBody, "<meta[^>]*name\\s*=\\s*['\"]?content-security-policy['\"]?[^>]*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
							}
							catch { }

							// Inject the base tag right after <head> opens (must be before any href/src attributes)
							var headOpenIdx = safeBody.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
							if (headOpenIdx < 0)
							{
								// Try with whitespace/attributes
								var match = System.Text.RegularExpressions.Regex.Match(safeBody, "<head[\\s>]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
								if (match.Success) headOpenIdx = match.Index;
							}

							if (headOpenIdx >= 0 && !string.IsNullOrEmpty(baseTag))
							{
								// Find the end of the <head> tag (after '>')
								var closeIdx = safeBody.IndexOf('>', headOpenIdx);
								if (closeIdx >= 0)
								{
									closeIdx++; // Move past '>'
									safeBody = safeBody.Substring(0, closeIdx) + baseTag + safeBody.Substring(closeIdx);
									Logger?.LogDebug("Injected base tag after <head> for {Path}", path);
								}
							}
							else if (!string.IsNullOrEmpty(baseTag) && safeBody.Length > 100)
							{
								// Only warn if this looks like actual HTML content (not tiny responses)
								Logger?.LogWarning("Could not find <head> tag to inject base tag for {Path} (content length: {Length}, starts with: {Preview})",
									path, safeBody.Length, safeBody.Substring(0, Math.Min(200, safeBody.Length)).Replace("\n", " ").Replace("\r", ""));
							}

							// Rewrite URLs in the HTML to work with the proxy
							if (!string.IsNullOrEmpty(proxyPrefix))
							{
								// Determine what we're proxying (mainsail or fluidd)
								string uiPath = "";
								if (proxyPrefix.Contains("mainsail", StringComparison.OrdinalIgnoreCase))
								{
									uiPath = "/mainsail/";
								}
								else if (proxyPrefix.Contains("fluidd", StringComparison.OrdinalIgnoreCase))
								{
									uiPath = "/fluidd/";
								}

								if (!string.IsNullOrEmpty(uiPath))
								{
									Logger?.LogDebug("Rewriting URLs: {UiPath} -> {ProxyPrefix}/", uiPath, proxyPrefix);

									// Rewrite absolute paths to use the proxy prefix
									// href="/mainsail/..." -> href="/proxy/mainsail/..."
									// src="/mainsail/..." -> src="/proxy/mainsail/..."
									safeBody = System.Text.RegularExpressions.Regex.Replace(
										safeBody,
										$@"(href|src)\s*=\s*([""'])({System.Text.RegularExpressions.Regex.Escape(uiPath)})",
										$"$1=$2{proxyPrefix}/",
										System.Text.RegularExpressions.RegexOptions.IgnoreCase
									);
								}
							}

							// Inject the proxy shim script before </head> closes
							var idx = safeBody.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
							var shimScript = injection.Substring(baseTag.Length); // Remove base tag since we already added it
							if (idx >= 0) result = safeBody.Substring(0, idx) + shimScript + safeBody.Substring(idx);
							else
							{
								idx = safeBody.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
								if (idx >= 0) result = safeBody.Substring(0, idx) + shimScript + safeBody.Substring(idx);
								else result = safeBody + shimScript;
							}
							var bytes = System.Text.Encoding.UTF8.GetBytes(result);
							await ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, ctx.RequestAborted);
						}
					}
					else
					{
						var bytes = System.Text.Encoding.UTF8.GetBytes(body ?? string.Empty);
						await ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, ctx.RequestAborted);
					}
				}
				else
				{
					// Stream directly without buffering
					await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
				}
			}
		}
		catch (OperationCanceledException oce)
		{
			// Differentiate between downstream client abort and upstream cancellation/timeouts
			if (ctx.RequestAborted.IsCancellationRequested)
			{
				// Don't log client cancellations for static assets (browser often cancels these)
				if (!path.Contains(".js") && !path.Contains(".css") && !path.Contains(".woff") && !path.Contains(".png") && !path.Contains(".jpg") && !path.Contains(".svg"))
				{
					Logger?.LogDebug("Client cancelled request while proxying {Path}", path);
				}
			}
			else
			{
				Logger?.LogWarning(oce, "Upstream request canceled/timeout while proxying {Path} to {TargetBase}", path, targetBase);
			}

			if (!ctx.Response.HasStarted)
			{
				ctx.Response.StatusCode = 502;
				try { await ctx.Response.WriteAsync("Proxy canceled: " + oce.Message); } catch { }
			}
		}
		catch (Exception ex)
		{
			Logger?.LogError(ex, "Error proxying {Path}", path);
			if (!ctx.Response.HasStarted)
			{
				ctx.Response.StatusCode = 502;
				try { await ctx.Response.WriteAsync("Proxy error: " + ex.Message); } catch { }
			}
		}
	}

	internal static async Task PumpWebSocket(System.Net.WebSockets.WebSocket from, System.Net.WebSockets.WebSocket to, CancellationToken ct)
	{
		var buffer = new byte[8192];
		try
		{
			while (from.State == System.Net.WebSockets.WebSocketState.Open && to.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
			{
				var result = await from.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
				if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
				{
					try { await to.CloseOutputAsync(result.CloseStatus ?? System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription, ct); } catch { }
					break;
				}
				// Log brief preview of text messages for debugging (avoid logging binary frames)
				try
				{
					if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
					{
						var text = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
						var preview = text.Length > 500 ? text.Substring(0, 500) + "..." : text;
						Logger?.LogDebug("WS Proxy message (len={Len}): {Preview}", result.Count, preview);
					}
				}
				catch (Exception ex)
				{
					Logger?.LogDebug(ex, "Failed to log WS message preview");
				}
				await to.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, ct);
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			Logger?.LogError(ex, "WS Proxy Pump error");
		}
	}
}

// Request models
internal class PrivacyUpdateRequest
{
	public string? Privacy { get; set; }
}

internal class ChatMessageRequest
{
	public string Message { get; set; } = string.Empty;
}
