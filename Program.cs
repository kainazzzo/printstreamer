using PrintStreamer.Timelapse;
using PrintStreamer.Services;
using PrintStreamer.Streamers;
using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Antiforgery;
using PrintStreamer.Overlay;
using FastEndpoints;
using System.Linq;

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
// OBS integration
webBuilder.Services.AddSingleton<IOBSService, OBSService>();
webBuilder.Services.AddSingleton<StreamService>();
webBuilder.Services.AddSingleton<StreamOrchestrator>();
webBuilder.Services.AddSingleton<PrintStreamOrchestrator>();
webBuilder.Services.AddSingleton<AudioService>();
webBuilder.Services.AddSingleton<AudioBroadcastService>();
// Printer console service (skeleton)
webBuilder.Services.AddSingleton<PrinterConsoleService>();
webBuilder.Services.AddSingleton<OverlayTextService>();
webBuilder.Services.AddSingleton<MoonrakerPoller>();

// Start the same singleton as a hosted service
webBuilder.Services.AddHostedService<ApplicationStartupHostedService>();


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
// Register FastEndpoints so the endpoint classes are discovered
webBuilder.Services.AddFastEndpoints();

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

// Application variables are now managed by hosted services

var app = webBuilder.Build();

// Service wiring and orchestration setup is now handled by ApplicationStartupHostedService

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

	// Service initialization and startup logic is now handled by ApplicationStartupHostedService

	// Enhanced test page with timelapse management
	// Blazor pages are now served via MapRazorComponents below

	// Map FastEndpoints and API controllers (printer control API, etc.)
	app.UseFastEndpoints();
	app.MapControllers();

	app.MapRazorComponents<PrintStreamer.App>()
		.AddInteractiveServerRenderMode();

	// Application lifecycle management is now handled by ApplicationStartupHostedService

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

	// Debug pipeline endpoint moved to FastEndpoints: Endpoints/Api/Debug/PipelineEndpoint.cs

}

// Start the host so IHostedService instances are started in all modes
await app.RunAsync();

// (ProxyUtil helper defined at top of file)

// Application cleanup is now handled by hosted services
// Poll/stream behavior and console monitoring are handled by ApplicationStartupHostedService when configured

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

// Small helpers to redact sensitive tokens from logged output and URLs.
// Keep these conservative and best-effort: do not attempt to fully parse every response.
internal static string RedactSensitive(string input)
{
    if (string.IsNullOrEmpty(input)) return input;
    try
    {
        var outStr = input;

        // redact common query param forms: access_token=VALUE
        outStr = System.Text.RegularExpressions.Regex.Replace(outStr, "(?i)([?&]access_token)=([^&\\s]+)", "$1=<redacted>");

        // redact JSON fields "access_token":"VALUE" and "refresh_token":"VALUE"
        outStr = System.Text.RegularExpressions.Regex.Replace(outStr, "(?i)(\"access_token\"\\s*:\\s*\")([^\"]+)(\")", "$1<redacted>$3");
        outStr = System.Text.RegularExpressions.Regex.Replace(outStr, "(?i)(\"refresh_token\"\\s*:\\s*\")([^\"]+)(\")", "$1<redacted>$3");
        outStr = System.Text.RegularExpressions.Regex.Replace(outStr, "(?i)(\"id_token\"\\s*:\\s*\")([^\"]+)(\")", "$1<redacted>$3");
        outStr = System.Text.RegularExpressions.Regex.Replace(outStr, "(?i)(\"token\"\\s*:\\s*\")([^\"]+)(\")", "$1<redacted>$3");

        // Generic key=value forms (best-effort)
        outStr = System.Text.RegularExpressions.Regex.Replace(outStr, "(?i)(access_token)\\s*=\\s*[^&\\s]+", "$1=<redacted>");

        return outStr;
    }
    catch
    {
        return input;
    }
}

internal static string SanitizeUrl(string url)
{
    if (string.IsNullOrEmpty(url)) return url;
    try
    {
        // redact access_token query param values in URLs
        return System.Text.RegularExpressions.Regex.Replace(url, "(?i)([?&])(access_token)=([^&]+)", "$1$2=<redacted>");
    }
    catch
    {
        return url;
    }
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
    Logger?.LogDebug("Moonraker Proxy {Method} {TargetUrl}", ctx.Request.Method, SanitizeUrl(targetUrl));
}
else if (path.StartsWith("assets/") || path.Contains(".js") || path.Contains(".css"))
{
    Logger?.LogDebug("Asset Proxy {Method} {Path} -> {TargetUrl}", ctx.Request.Method, path, SanitizeUrl(targetUrl));
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
// Redact sensitive tokens and normalize newlines before truncation
var logBody = RedactSensitive(body ?? string.Empty).Replace(Environment.NewLine, " ");
if (logBody.Length > 1000) logBody = logBody.Substring(0, 1000) + "...";
Logger?.LogDebug("Proxy Response {StatusCode} ({ContentType};len={ContentLength}) from {TargetUrl}: {Body}", response.StatusCode, contentType, contentLength, SanitizeUrl(targetUrl), logBody);

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
