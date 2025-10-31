﻿using PrintStreamer.Timelapse;
using PrintStreamer.Services;
using System.Diagnostics;

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

// Load custom configuration file (the filename itself comes from the config we just loaded)
// This allows user-modified settings to be persisted to a separate file
var customConfigFile = webBuilder.Configuration.GetValue<string>("CustomConfigFile");
if (!string.IsNullOrWhiteSpace(customConfigFile))
{
	var customConfigPath = Path.Combine(Directory.GetCurrentDirectory(), customConfigFile);
	Console.WriteLine($"[Config] Loading custom configuration from: {customConfigFile}");
	
	// Add the custom config file to the configuration builder
	// It will override values from appsettings.json if they exist
	webBuilder.Configuration.AddJsonFile(customConfigFile, optional: true, reloadOnChange: true);
}

var config = webBuilder.Configuration;

// Generate fallback_black.jpg at startup if it doesn't exist
// This file is used by WebCamManager when the camera source is unavailable
var fallbackImagePath = Path.Combine(Directory.GetCurrentDirectory(), "fallback_black.jpg");
if (!File.Exists(fallbackImagePath))
{
	try
	{
		Console.WriteLine("[Startup] Generating fallback_black.jpg...");
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
				Console.WriteLine("[Startup] fallback_black.jpg created successfully");
			}
			else
			{
				Console.WriteLine($"[Startup] Warning: ffmpeg exited with code {proc.ExitCode} when creating fallback image");
			}
		}
	}
	catch (Exception ex)
	{
		Console.WriteLine($"[Startup] Warning: Failed to generate fallback_black.jpg: {ex.Message}");
	}
}

// Register application services
webBuilder.Services.AddSingleton<TimelapseManager>();
webBuilder.Services.AddSingleton<PrintStreamer.Services.WebCamManager>();
webBuilder.Services.AddSingleton<PrintStreamer.Services.StreamService>();
webBuilder.Services.AddSingleton<PrintStreamer.Services.StreamOrchestrator>();
webBuilder.Services.AddSingleton<PrintStreamer.Services.MoonrakerPollerService>();
webBuilder.Services.AddHostedService<PrintStreamer.Services.MoonrakerHostedService>();

// Add Blazor Server services
webBuilder.Services.AddRazorComponents()
	.AddInteractiveServerComponents();
webBuilder.Services.AddHttpClient();

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

// Add Blazor Server middleware
app.UseAntiforgery();

if (serveEnabled)
{
	// Enable WebSocket support (required for Mainsail/Fluidd)
	app.UseWebSockets();
	// Start ASP.NET Core minimal server to proxy the MJPEG source to clients on /stream
	if (string.IsNullOrWhiteSpace(source))
	{
		Console.WriteLine("Error: --source is required when running in --serve mode.\n");
		PrintHelp();
		return;
	}

	var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    
	// Resolve managers from DI
	var webcamManager = app.Services.GetRequiredService<PrintStreamer.Services.WebCamManager>();
	// Resolve timelapse manager from DI (registered earlier)
	timelapseManager = app.Services.GetRequiredService<TimelapseManager>();

	// Architecture Overview:
	// 1. WebCam Proxy (/stream) - Proxies MJPEG webcam, handles camera simulation
	// 2. Local HLS Stream - ffmpeg reads /stream and outputs HLS (always runs when Stream:Local:Enabled=true)
	// 3. YouTube Live Broadcast - OAuth creates broadcast, restarts ffmpeg to add RTMP output
	
	// Note: HLS stream will be started AFTER the web server is listening (see below)

// (Local HLS preview startup will be handled in the Serve static-file block below so we only declare the variables once.)

	app.MapGet("/stream", async (HttpContext ctx) => await webcamManager.HandleStreamRequest(ctx));

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
		var webcamManager = ctx.RequestServices.GetRequiredService<PrintStreamer.Services.WebCamManager>();
		return Results.Json(new { disabled = webcamManager.IsDisabled });
	});

	app.MapPost("/api/camera/on", async (HttpContext ctx) =>
	{
		var webcamManager = ctx.RequestServices.GetRequiredService<PrintStreamer.Services.WebCamManager>();
		var streamService = ctx.RequestServices.GetRequiredService<PrintStreamer.Services.StreamService>();
		webcamManager.SetDisabled(false);
		Console.WriteLine("Camera simulation: enabled (camera on)");
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
				Console.WriteLine($"Failed to restart stream: {ex.Message}");
			}
		}
		return Results.Json(new { disabled = webcamManager.IsDisabled });
	});

	app.MapPost("/api/camera/off", async (HttpContext ctx) =>
	{
		var webcamManager = ctx.RequestServices.GetRequiredService<PrintStreamer.Services.WebCamManager>();
		var streamService = ctx.RequestServices.GetRequiredService<PrintStreamer.Services.StreamService>();
		webcamManager.SetDisabled(true);
		Console.WriteLine("Camera simulation: disabled (camera off)");
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
				Console.WriteLine($"Failed to restart stream: {ex.Message}");
			}
		}
		return Results.Json(new { disabled = webcamManager.IsDisabled });
	});

	app.MapPost("/api/camera/toggle", async (HttpContext ctx) =>
	{
		var webcamManager = ctx.RequestServices.GetRequiredService<PrintStreamer.Services.WebCamManager>();
		var streamService = ctx.RequestServices.GetRequiredService<PrintStreamer.Services.StreamService>();
		webcamManager.Toggle();
		var newVal = webcamManager.IsDisabled;
		Console.WriteLine($"Camera simulation: toggled -> disabled={newVal}");
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
				Console.WriteLine($"Failed to restart stream: {ex.Message}");
			}
		}
		return Results.Json(new { disabled = newVal });
	});

	// Reverse proxy for Mainsail/Fluidd to bypass X-Frame-Options and same-origin issues
	app.MapGet("/proxy/mainsail/{**path}", async (HttpContext ctx, string? path) =>
	{
		var target = config.GetValue<string>("PrinterUI:MainsailUrl");
		Console.WriteLine($"[Proxy] GET /proxy/mainsail/{path ?? ""} -> target={target ?? "NOT CONFIGURED"}");
		if (string.IsNullOrWhiteSpace(target))
		{
			ctx.Response.StatusCode = 404;
			await ctx.Response.WriteAsync("Mainsail URL not configured");
			return;
		}
		await ProxyUtil.ProxyRequest(ctx, target, path ?? "");
	});

	app.MapGet("/proxy/fluidd/{**path}", async (HttpContext ctx, string? path) =>
	{
		var target = config.GetValue<string>("PrinterUI:FluiddUrl");
		Console.WriteLine($"[Proxy] GET /proxy/fluidd/{path ?? ""} -> target={target ?? "NOT CONFIGURED"}");
		if (string.IsNullOrWhiteSpace(target))
		{
			ctx.Response.StatusCode = 404;
			await ctx.Response.WriteAsync("Fluidd URL not configured");
			return;
		}
		await ProxyUtil.ProxyRequest(ctx, target, path ?? "");
	});

	// Also handle root without trailing catch-all
	app.MapGet("/proxy/mainsail", async (HttpContext ctx) =>
	{
		var target = config.GetValue<string>("PrinterUI:MainsailUrl");
		if (string.IsNullOrWhiteSpace(target)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Mainsail URL not configured"); return; }
		Console.WriteLine($"[Proxy] GET /proxy/mainsail -> {target}");
		await ProxyUtil.ProxyRequest(ctx, target, "");
	});

	app.MapGet("/proxy/fluidd", async (HttpContext ctx) =>
	{
		var target = config.GetValue<string>("PrinterUI:FluiddUrl");
		if (string.IsNullOrWhiteSpace(target)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Fluidd URL not configured"); return; }
		Console.WriteLine($"[Proxy] GET /proxy/fluidd -> {target}");
		await ProxyUtil.ProxyRequest(ctx, target, "");
	});

	// Support absolute-root asset paths emitted by the apps (e.g., /mainsail/assets/...)
	app.MapMethods("/mainsail/{**path}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }, async (HttpContext ctx, string? path) =>
	{
		var target = config.GetValue<string>("PrinterUI:MainsailUrl");
		Console.WriteLine($"[Proxy] {ctx.Request.Method} /mainsail/{path ?? ""} -> target={target ?? "NOT CONFIGURED"}");
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
		app.Map("/websocket", async (HttpContext ctx) =>
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

			using var upstream = new System.Net.WebSockets.ClientWebSocket();
			upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
			// Propagate selected headers (auth, cookies, origin). Skip WS handshake headers.
			string[] skipHeaders = new[]
			{
				"Connection","Upgrade","Sec-WebSocket-Key","Sec-WebSocket-Version","Sec-WebSocket-Extensions","Sec-WebSocket-Protocol","Host"
			};
			foreach (var h in ctx.Request.Headers)
			{
				if (Array.Exists(skipHeaders, k => k.Equals(h.Key, StringComparison.OrdinalIgnoreCase)))
					continue;
				try { upstream.Options.SetRequestHeader(h.Key, h.Value); } catch { }
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

			Console.WriteLine($"[WS Proxy] Connecting upstream {upstreamUri} (Origin={ctx.Request.Headers["Origin"]})");
			try
			{
				await upstream.ConnectAsync(upstreamUri, ctx.RequestAborted);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[WS Proxy] Upstream connect failed: {ex.Message} [{upstreamUri}]");
				// Do NOT accept the downstream socket if upstream failed; return 502 instead
				ctx.Response.StatusCode = 502;
				await ctx.Response.WriteAsync("Upstream websocket connect failed");
				return;
			}

			// Only accept downstream after upstream is connected to avoid starting the response prematurely
			Console.WriteLine("[WS Proxy] Upstream connected, accepting downstream...");
			using var downstream = await ctx.WebSockets.AcceptWebSocketAsync();

			var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
			var pump1 = ProxyUtil.PumpWebSocket(downstream, upstream, cts.Token);
			var pump2 = ProxyUtil.PumpWebSocket(upstream, downstream, cts.Token);
			await Task.WhenAny(pump1, pump2);
			cts.Cancel();
			Console.WriteLine("[WS Proxy] Tunnel closed");
		});
	}

	// Config API endpoints (lightweight state)
	app.MapGet("/api/config/state", (HttpContext ctx) =>
	{
		var autoBroadcastEnabled = config.GetValue<bool>("YouTube:LiveBroadcast:Enabled");
		var autoUploadEnabled = config.GetValue<bool>("YouTube:TimelapseUpload:Enabled");
		return Results.Json(new { autoBroadcastEnabled, autoUploadEnabled });
	});

	app.MapPost("/api/config/auto-broadcast", (HttpContext ctx) =>
	{
		var raw = ctx.Request.Query["enabled"].ToString();
		bool enabled;
		if (!bool.TryParse(raw, out enabled))
		{
			// Accept "1"/"0" as well for robustness
			enabled = raw == "1";
		}
		config["YouTube:LiveBroadcast:Enabled"] = enabled.ToString();
		Console.WriteLine($"Auto-broadcast: {(enabled ? "enabled" : "disabled")}");
		return Results.Json(new { success = true, enabled });
	});

	app.MapPost("/api/config/auto-upload", (HttpContext ctx) =>
	{
		var raw = ctx.Request.Query["enabled"].ToString();
		bool enabled;
		if (!bool.TryParse(raw, out enabled))
		{
			enabled = raw == "1";
		}
		config["YouTube:TimelapseUpload:Enabled"] = enabled.ToString();
		Console.WriteLine($"Auto-upload timelapses: {(enabled ? "enabled" : "disabled")}");
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

	app.MapGet("/api/live/status", (HttpContext ctx) =>
	{
		try
		{
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			var isLive = orchestrator.IsBroadcastActive;
			var broadcastId = orchestrator.CurrentBroadcastId;
			var streamerRunning = orchestrator.IsStreaming;
			var waitingForIngestion = orchestrator.IsWaitingForIngestion;

			// Check if HLS manifest exists
			var hlsFolder = ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue<string>("Stream:Local:HlsFolder") ?? "hls";
			var hlsManifest = Path.Combine(Directory.GetCurrentDirectory(), hlsFolder, "stream.m3u8");
			var hlsAvailable = File.Exists(hlsManifest);

			// Kick a background self-heal if HLS is missing but streaming is expected
			if (!hlsAvailable && (streamerRunning || isLive))
			{
				_ = Task.Run(async () =>
				{
					try { await orchestrator.EnsureStreamingHealthyAsync(true, CancellationToken.None); } catch { }
				});
			}

			return Results.Json(new { isLive, broadcastId, streamerRunning, waitingForIngestion, hlsAvailable });
		}
		catch (Exception ex)
		{
			return Results.Json(new { isLive = false, broadcastId = (string?)null, streamerRunning = false, waitingForIngestion = false, hlsAvailable = false, error = ex.Message });
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

	// Manual repair endpoint: ensure HLS/stream health and recover broadcast if possible
	app.MapPost("/api/live/repair", async (HttpContext ctx) =>
	{
		try
		{
			var orchestrator = ctx.RequestServices.GetRequiredService<StreamOrchestrator>();
			var ok = await orchestrator.EnsureStreamingHealthyAsync(true, ctx.RequestAborted);
			return Results.Json(new { success = ok });
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

	app.MapPost("/api/timelapses/{name}/generate", async (string name, HttpContext ctx) =>
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
			
			Console.WriteLine($"[API] Generating video from {frameFiles.Length} frames: {videoPath}");
			
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
				Console.WriteLine($"[API] Video created successfully: {videoPath}");
				return Results.Json(new { success = true, videoPath });
			}
			else
			{
				Console.WriteLine($"[API] ffmpeg failed with exit code {proc.ExitCode}");
				Console.WriteLine($"[API] ffmpeg output: {output}");
				return Results.Json(new { success = false, error = $"ffmpeg failed with exit code {proc.ExitCode}" });
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[API] Error generating video: {ex.Message}");
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	app.MapPost("/api/timelapses/{name}/upload", async (string name, HttpContext ctx) =>
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

			// Use YouTubeControlService to upload
			var ytService = new YouTubeControlService(config);
			if (!await ytService.AuthenticateAsync(ctx.RequestAborted))
			{
				ytService.Dispose();
				return Results.Json(new { success = false, error = "YouTube authentication failed" });
			}

			// Bypass upload config for manual UI uploads
			var videoId = await ytService.UploadTimelapseVideoAsync(videoPath, name, ctx.RequestAborted, true);
			ytService.Dispose();

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
					Console.WriteLine($"[API] Failed to save YouTube URL to metadata: {ex.Message}");
				}
				
				return Results.Json(new { success = true, videoId, url });
			}
			return Results.Json(new { success = false, error = "Upload failed" });
		}
		catch (Exception ex)
		{
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

	// Enhanced test page with timelapse management
	// Blazor pages are now served via MapRazorComponents below
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
						Enabled = config.GetValue<bool?>("Stream:Local:Enabled") ?? false,
						HlsFolder = config.GetValue<string>("Stream:Local:HlsFolder") ?? "hls"
					}
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
					BoxBorderW = config.GetValue<int?>("Overlay:BoxBorderW") ?? 8,
					X = config.GetValue<string>("Overlay:X") ?? "(w-tw)-20",
					Y = config.GetValue<string>("Overlay:Y") ?? "20"
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

	// Save configuration
	app.MapPost("/api/config", async (HttpContext ctx) =>
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
			
			Console.WriteLine($"[Config] Saving configuration to: {customConfigFile}");
			
			// Parse and re-serialize with indentation for pretty formatting
			var jsonDoc = System.Text.Json.JsonDocument.Parse(body);
			var options = new System.Text.Json.JsonSerializerOptions
			{
				WriteIndented = true,
				PropertyNamingPolicy = null
			};
			
			var jsonString = System.Text.Json.JsonSerializer.Serialize(jsonDoc.RootElement, options);
			await File.WriteAllTextAsync(configPath, jsonString);
			
			Console.WriteLine($"[Config] Configuration saved to {customConfigFile}");
			return Results.Json(new { success = true, message = "Configuration saved. Restart required for changes to take effect." });
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Config] Error saving configuration: {ex.Message}");
			return Results.Json(new { success = false, error = ex.Message });
		}
	});

	// Reset configuration to defaults
	app.MapPost("/api/config/reset", async (HttpContext ctx) =>
	{
		try
		{
			var defaultConfig = new
			{
				Stream = new
				{
					Source = "http://192.168.1.2/webcam"
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
					Template = "Nozzle: {nozzle:0}°C/{nozzleTarget:0}°C | Bed: {bed:0}°C/{bedTarget:0}°C | Layer {layers} | {progress:0}%\nSpd:{speed}mm/s | Flow:{flow} | Fil:{filament}m | ETA:{eta:hh:mm tt}",
					FontFile = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
					FontSize = 16,
					FontColor = "white",
					Box = true,
					BoxColor = "black@0.4",
					BoxBorderW = 8,
					X = "(w-tw)-20",
					Y = "20"
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
			
			Console.WriteLine("[Config] Configuration reset to defaults");
			return Results.Json(new { success = true, message = "Configuration reset to defaults" });
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Config] Error resetting configuration: {ex.Message}");
			return Results.Json(new { success = false, error = ex.Message });
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

	// Start local HLS stream AFTER the web server is listening (to avoid race condition)
	lifetime.ApplicationStarted.Register(() =>
	{
		if (config.GetValue<bool?>("Stream:Local:Enabled") ?? false)
		{
			Console.WriteLine("[Stream] Web server ready, starting local HLS preview stream...");
			// Start a local HLS-only stream on startup for preview
			Task.Delay(500).ContinueWith(async _ =>
			{
				try
				{
					var streamService = app.Services.GetRequiredService<StreamService>();
					if (!streamService.IsStreaming)
					{
						Console.WriteLine("[Stream] Starting local HLS preview stream");
						await streamService.StartStreamAsync(null, null, CancellationToken.None);
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[Stream] Failed to start local preview: {ex.Message}");
				}
			});
		}
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
	Console.WriteLine("  2. Local HLS Stream - ffmpeg reads /stream and outputs HLS segments");
	Console.WriteLine("  3. YouTube Live Broadcast - OAuth creates broadcast, adds RTMP output to HLS stream");
	Console.WriteLine();
	Console.WriteLine("Configuration Methods:");
	Console.WriteLine("  1. appsettings.json (base configuration)");
	Console.WriteLine("  2. Environment variables (use __ for nested keys, e.g., Stream__Source)");
	Console.WriteLine("  3. Command-line arguments (use --Key value or --Key=value)");
	Console.WriteLine();
	Console.WriteLine("Required Configuration:");
	Console.WriteLine("  Stream:Source                     - MJPEG webcam URL (required)");
	Console.WriteLine("  Stream:Local:Enabled              - Enable local HLS stream (true/false)");
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
	internal static readonly HttpClient Client = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(60)
	};

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
				Console.WriteLine($"[Moonraker Proxy] {ctx.Request.Method} {targetUrl}");
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
				Console.WriteLine($"[Proxy] {response.StatusCode} from {targetUrl}");
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

				if (shouldBuffer)
				{
					var body = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
					// Log truncated body for diagnostics (redact tokens)
					var logBody = body?.Replace(Environment.NewLine, " ") ?? string.Empty;
					if (logBody.Length > 1000) logBody = logBody.Substring(0, 1000) + "...";
					Console.WriteLine($"[Proxy] Response {response.StatusCode} ({contentType};len={contentLength}) from {targetUrl}: {logBody}");

					// If HTML 200, inject WS shim before writing
					if ((int)response.StatusCode == 200 && !string.IsNullOrWhiteSpace(contentType) && contentType.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						// Enhanced injection: Clear all storage types and intercept both WebSocket and fetch/XMLHttpRequest for Mainsail
						var injection = @"<script>(function(){
try{
  console.log('[Proxy Shim] Loading enhanced Mainsail/Fluidd proxy shim...');
  
  // WebSocket proxy - intercept and rewrite to our proxy
  var WS=window.WebSocket;
  function rw(u){
    try{
      if(!u)return u;
      var x;
      try{x=new URL(u, window.location.href);}catch(e){return u;}
      var s=x.search||'';
      var rewritten = new URL('/websocket'+s, window.location.href).toString();
      console.log('[WS Shim] Rewriting WebSocket:',u,'->',rewritten);
      return rewritten;
    }catch(e){
      console.error('[WS Shim] Error rewriting:',e);
      return u;
    }
  }
  window.WebSocket=function(u,p){
    var nu=rw(u);
    try{return p?new WS(nu,p):new WS(nu);}catch(e){return new WS(nu);}
  };
  window.WebSocket.prototype=WS.prototype;
  
  // Clear ALL storage types for Mainsail (it uses localStorage, sessionStorage, and IndexedDB)
  console.log('[Proxy Shim] Clearing all browser storage...');
  try{
    localStorage.clear();
    console.log('[Proxy Shim] localStorage cleared');
  }catch(e){console.error('[Proxy Shim] localStorage clear failed:',e);}
  
  try{
    sessionStorage.clear();
    console.log('[Proxy Shim] sessionStorage cleared');
  }catch(e){console.error('[Proxy Shim] sessionStorage clear failed:',e);}
  
  try{
    if(window.indexedDB){
      // Mainsail stores connection info in IndexedDB - we need to clear it
      indexedDB.databases().then(function(dbs){
        console.log('[Proxy Shim] Found IndexedDB databases:',dbs.map(function(d){return d.name;}));
        dbs.forEach(function(db){
          try{
            console.log('[Proxy Shim] Deleting IndexedDB:',db.name);
            indexedDB.deleteDatabase(db.name);
          }catch(e){console.error('[Proxy Shim] IndexedDB delete failed:',e);}
        });
      }).catch(function(e){console.error('[Proxy Shim] IndexedDB enumeration failed:',e);});
    }
  }catch(e){console.error('[Proxy Shim] IndexedDB clear failed:',e);}
  
  // Unregister service workers that might cache old connection info
  try{
    if('serviceWorker' in navigator){
      navigator.serviceWorker.getRegistrations().then(function(regs){
        console.log('[Proxy Shim] Found',regs.length,'service workers');
        regs.forEach(function(r){
          try{
            console.log('[Proxy Shim] Unregistering service worker:',r.scope);
            r.unregister();
          }catch(e){console.error('[Proxy Shim] SW unregister failed:',e);}
        });
      });
    }
  }catch(e){console.error('[Proxy Shim] Service worker clear failed:',e);}
  
  // Clear caches
  try{
    if(window.caches){
      caches.keys().then(function(keys){
        console.log('[Proxy Shim] Found cache keys:',keys);
        keys.forEach(function(k){
          try{
            console.log('[Proxy Shim] Deleting cache:',k);
            caches.delete(k);
          }catch(e){console.error('[Proxy Shim] Cache delete failed:',e);}
        });
      });
    }
  }catch(e){console.error('[Proxy Shim] Cache clear failed:',e);}
  
  console.log('[Proxy Shim] Mainsail/Fluidd proxy shim loaded successfully');
}catch(e){console.error('[Proxy Shim] Critical error:',e);}
})();</script>";
						string result;
						// Remove any meta Content-Security-Policy tags so our injected inline script can run
						var safeBody = (body ?? string.Empty);
						try
						{
							safeBody = System.Text.RegularExpressions.Regex.Replace(safeBody, "<meta[^>]*http-equiv\\s*=\\s*['\"]?Content-Security-Policy['\"]?[^>]*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
							safeBody = System.Text.RegularExpressions.Regex.Replace(safeBody, "<meta[^>]*name\\s*=\\s*['\"]?content-security-policy['\"]?[^>]*>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
						}
						catch { }

						var idx = safeBody.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
						if (idx >= 0) result = safeBody.Substring(0, idx) + injection + safeBody.Substring(idx);
						else
						{
							idx = safeBody.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
							if (idx >= 0) result = safeBody.Substring(0, idx) + injection + safeBody.Substring(idx);
							else result = safeBody + injection;
						}
						var bytes = System.Text.Encoding.UTF8.GetBytes(result);
						await ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length, ctx.RequestAborted);
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
		catch (Exception ex)
		{
			Console.WriteLine($"[Proxy] Error proxying {path}: {ex.Message}");
			if (!ctx.Response.HasStarted)
			{
				ctx.Response.StatusCode = 502;
				await ctx.Response.WriteAsync("Proxy error: "+ ex.Message);
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
				await to.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, ct);
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			Console.WriteLine($"[WS Proxy] Pump error: {ex.Message}");
		}
	}
}
