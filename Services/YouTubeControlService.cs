using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
// Google.Apis.Util.Store is used for IDataStore implementations
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using System.Text.Json;
using Google.Apis.Util.Store;
// using Google.Apis.Auth.OAuth2.Flows; // not used with the standard broker flow
using System.Collections.Concurrent;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Auth.OAuth2.Requests;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PrintStreamer.Services;

internal class YouTubeControlService : IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<YouTubeControlService> _logger;
    private readonly YouTubePollingManager? _pollingManager;
    private readonly MoonrakerClient _moonrakerClient;
    private Google.Apis.YouTube.v3.YouTubeService? _youtubeService;
    private UserCredential? _credential; // Used for user OAuth flow
    private readonly string _tokenPath;
    private readonly InMemoryDataStore _inMemoryStore = new InMemoryDataStore();
    private Task? _refreshTask;
    private CancellationTokenSource? _refreshCts;

    public YouTubeControlService(IConfiguration config, ILogger<YouTubeControlService> logger, YouTubePollingManager? pollingManager = null, MoonrakerClient? moonrakerClient = null)
    {
        _config = config;
        _logger = logger;
        _pollingManager = pollingManager;
        _moonrakerClient = moonrakerClient ?? throw new ArgumentNullException(nameof(moonrakerClient));
        _tokenPath = Path.Combine(Directory.GetCurrentDirectory(), "tokens", "youtube_token.json");
    }

    /// <summary>
    /// Sets the thumbnail for a YouTube broadcast using a JPEG image.
    /// </summary>
    public async Task<bool> SetBroadcastThumbnailAsync(string broadcastId, byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            _logger.LogWarning("YouTube service not initialized. Call AuthenticateAsync first.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(broadcastId))
        {
            _logger.LogWarning("Invalid broadcastId for thumbnail update.");
            return false;
        }
        if (imageBytes == null || imageBytes.Length == 0)
        {
            _logger.LogWarning("Invalid image data for thumbnail update (null or empty).");
            return false;
        }
        _logger.LogInformation("Uploading thumbnail: broadcastId={BroadcastId}, size={Size} bytes", broadcastId, imageBytes.Length);
        try
        {
            using var ms = new MemoryStream(imageBytes);
            var thumbnailSetRequest = _youtubeService.Thumbnails.Set(broadcastId, ms, "image/jpeg");
            var response = await thumbnailSetRequest.UploadAsync(cancellationToken);
            if (response.Status == Google.Apis.Upload.UploadStatus.Completed)
            {
                _logger.LogInformation("Thumbnail updated for broadcast {BroadcastId}.", broadcastId);
                return true;
            }
            else
            {
                _logger.LogWarning("Thumbnail upload failed: {Status}", response.Status);
                if (response.Exception != null)
                {
                    _logger.LogError(response.Exception, "Upload exception while setting thumbnail");
                    if (response.Exception.InnerException != null)
                    {
                        _logger.LogError(response.Exception.InnerException, "Inner exception during thumbnail upload");
                    }
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating thumbnail");
            return false;
        }
    }

    /// <summary>
    /// Sets the thumbnail for an uploaded video using a JPEG image.
    /// </summary>
    public async Task<bool> SetVideoThumbnailAsync(string videoId, byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        // Reuse the same underlying Thumbnails.Set API; it accepts a video ID as well.
        return await SetBroadcastThumbnailAsync(videoId, imageBytes, cancellationToken);
    }

    /// <summary>
    /// Upload a timelapse video to YouTube as a regular video (not a live stream).
    /// </summary>
    public async Task<string?> UploadTimelapseVideoAsync(string videoFilePath, string? filename = null, CancellationToken cancellationToken = default, bool bypassUploadConfig = false)
    {
        // Check if timelapse upload is enabled, unless bypassing for manual UI upload
        var uploadEnabled = _config.GetValue<bool>("YouTube:TimelapseUpload:Enabled");
        if (!uploadEnabled && !bypassUploadConfig)
        {
            _logger.LogInformation("Timelapse upload is disabled in configuration.");
            return null;
        }

        if (_youtubeService == null)
        {
            _logger.LogWarning("YouTube service not initialized. Call AuthenticateAsync first.");
            return null;
        }
        if (!File.Exists(videoFilePath))
        {
            _logger.LogWarning("Video file not found: {VideoFile}", videoFilePath);
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(videoFilePath);
            _logger.LogInformation("Uploading timelapse video: {Path} ({Bytes} bytes)", videoFilePath, fileInfo.Length);

            // Build title and description from config and filename
            var baseTitle = _config["YouTube:LiveBroadcast:Title"] ?? "Print Streamer";
            var baseDescription = _config["YouTube:LiveBroadcast:Description"] ?? "3D print timelapse";
            var privacy = _config["YouTube:TimelapseUpload:Privacy"] ?? _config["YouTube:LiveBroadcast:Privacy"] ?? "unlisted";
            var categoryId = _config["YouTube:TimelapseUpload:CategoryId"] ?? _config["YouTube:LiveBroadcast:CategoryId"] ?? "28";

            // Clean up filename for title
            string cleanFilename = "Unknown Print";
            if (!string.IsNullOrWhiteSpace(filename))
            {
                cleanFilename = Path.GetFileNameWithoutExtension(filename);
            }

            var title = $"{baseTitle} - {cleanFilename} - Timelapse";
            var attribution = "Created with PrintStreamer — https://github.com/kainazzzo/printstreamer";
            var details = "";
            try
            {
                var moonBase = _config["Moonraker:BaseUrl"];
                Uri? baseUri = null;
                if (!string.IsNullOrWhiteSpace(moonBase) && Uri.TryCreate(moonBase, UriKind.Absolute, out var cfgUri))
                {
                    baseUri = cfgUri;
                }
                else
                {
                    var src = _config["Stream:Source"];
                    if (!string.IsNullOrWhiteSpace(src)) baseUri = _moonrakerClient.GetPrinterBaseUriFromStreamSource(src);
                }
                if (baseUri != null)
                {
                    var apiKey = _config["Moonraker:ApiKey"];
                    var authHeader = _config["Moonraker:AuthHeader"];
                    var info = await _moonrakerClient.GetPrintInfoAsync(baseUri, apiKey, authHeader, cancellationToken);
                    if (info != null)
                    {
                        var detailsList = new System.Collections.Generic.List<string>();
                        if (info.BedTempActual.HasValue || info.BedTempTarget.HasValue)
                        {
                            detailsList.Add($"Bed: {info.BedTempActual?.ToString("F1") ?? "n/a"}°C / {info.BedTempTarget?.ToString("F1") ?? "n/a"}°C");
                        }
                        if (info.Tool0Temp.HasValue)
                        {
                            var t = info.Tool0Temp.Value;
                            detailsList.Add($"Nozzle: {t.Actual?.ToString("F1") ?? "n/a"}°C / {t.Target?.ToString("F1") ?? "n/a"}°C");
                        }
                        if (!string.IsNullOrWhiteSpace(info.FilamentType) || !string.IsNullOrWhiteSpace(info.FilamentColor) || !string.IsNullOrWhiteSpace(info.FilamentBrand))
                        {
                            var fil = $"Filament: {info.FilamentBrand ?? ""} {info.FilamentType ?? ""} {info.FilamentColor ?? ""}".Trim();
                            detailsList.Add(fil);
                        }
                        if (info.FilamentUsedMm.HasValue || info.FilamentTotalMm.HasValue)
                        {
                            detailsList.Add($"Filament Used: {info.FilamentUsedMm?.ToString("F0") ?? "n/a"}mm / {info.FilamentTotalMm?.ToString("F0") ?? "n/a"}mm");
                        }
                        if (detailsList.Count > 0)
                        {
                            details = "\n\n" + string.Join("\n", detailsList);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Moonraker details fetch failed: {Message}", ex.Message);
            }
            var description = $"{baseDescription}\n\nTimelapse of {cleanFilename}\n\n{attribution}{details}";

            var video = new Video
            {
                Snippet = new VideoSnippet
                {
                    Title = title,
                    Description = description,
                    CategoryId = categoryId
                },
                Status = new VideoStatus
                {
                    PrivacyStatus = privacy,
                    SelfDeclaredMadeForKids = false
                }
            };

            using var fileStream = new FileStream(videoFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var videosInsertRequest = _youtubeService.Videos.Insert(video, "snippet,status", fileStream, "video/*");
            
            // Track upload progress
            videosInsertRequest.ProgressChanged += progress =>
            {
                switch (progress.Status)
                {
                        case Google.Apis.Upload.UploadStatus.Uploading:
                        var percent = (progress.BytesSent * 100.0) / fileStream.Length;
                        _logger.LogInformation("Upload progress: {Percent} ({BytesSent}/{Total} bytes)", $"{percent:F1}%", progress.BytesSent, fileStream.Length);
                        break;
                    case Google.Apis.Upload.UploadStatus.Completed:
                        _logger.LogInformation("Upload completed successfully!");
                        break;
                    case Google.Apis.Upload.UploadStatus.Failed:
                        _logger.LogError(progress.Exception, "Upload failed");
                        break;
                }
            };

            videosInsertRequest.ResponseReceived += uploadedVideo =>
            {
                _logger.LogInformation("Video uploaded successfully! Video ID: {VideoId}", uploadedVideo.Id);
                _logger.LogInformation("Video URL: https://www.youtube.com/watch?v={VideoId}", uploadedVideo.Id);
            };

            var uploadedVideoResponse = await videosInsertRequest.UploadAsync(cancellationToken);
            
            if (uploadedVideoResponse.Status == Google.Apis.Upload.UploadStatus.Completed)
            {
                var videoId = videosInsertRequest.ResponseBody?.Id;
                _logger.LogInformation("Timelapse video uploaded successfully! ID: {VideoId}", videoId);
                return videoId;
            }
            else
            {
                _logger.LogWarning("Video upload failed with status: {Status}", uploadedVideoResponse.Status);
                if (uploadedVideoResponse.Exception != null)
                {
                    _logger.LogError(uploadedVideoResponse.Exception, "Exception while uploading video");
                }
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading timelapse video");
            return null;
        }
    }

    /// <summary>
    /// Ensure a playlist with the given name exists. Returns its ID.
    /// If not found, creates it with the specified privacy status.
    /// </summary>
    public async Task<string?> EnsurePlaylistAsync(string name, string privacy = "unlisted", CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            _logger.LogWarning("YouTube service not initialized. Call AuthenticateAsync first.");
            return null;
        }

        try
        {
            // Try to find existing playlist by name (first 50 owned playlists)
            var list = _youtubeService.Playlists.List("snippet,status");
            list.Mine = true;
            list.MaxResults = 50;
            var response = await list.ExecuteAsync(cancellationToken);
            var existing = response.Items?.FirstOrDefault(p => string.Equals(p.Snippet?.Title, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return existing.Id;
            }

            // Create new playlist
            var playlist = new Google.Apis.YouTube.v3.Data.Playlist
            {
                Snippet = new Google.Apis.YouTube.v3.Data.PlaylistSnippet
                {
                    Title = name,
                    Description = $"Auto-managed by PrintStreamer: {name}"
                },
                Status = new Google.Apis.YouTube.v3.Data.PlaylistStatus
                {
                    PrivacyStatus = privacy
                }
            };

            var insert = _youtubeService.Playlists.Insert(playlist, "snippet,status");
            var created = await insert.ExecuteAsync(cancellationToken);
            _logger.LogInformation("[YouTube] Created playlist '{Name}' (ID: {Id})", name, created.Id);
            return created.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[YouTube] Failed to ensure playlist '{Name}': {Message}", name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Add a video to a playlist by ID. Returns true if added.
    /// </summary>
    public async Task<bool> AddVideoToPlaylistAsync(string playlistId, string videoId, CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            _logger.LogWarning("YouTube service not initialized. Call AuthenticateAsync first.");
            return false;
        }

        try
        {
            // First, check if the video is already in the playlist to avoid duplicates
            string? pageToken = null;
            do
            {
                var listReq = _youtubeService.PlaylistItems.List("id,snippet,contentDetails");
                listReq.PlaylistId = playlistId;
                listReq.MaxResults = 50;
                listReq.PageToken = pageToken;
                var listResp = await listReq.ExecuteAsync(cancellationToken);
                var exists = listResp.Items?.Any(i => i.Snippet?.ResourceId?.VideoId == videoId) == true;
                if (exists)
                {
                    _logger.LogInformation("[YouTube] Video {VideoId} already present in playlist {PlaylistId}; skipping add.", videoId, playlistId);
                    return true;
                }
                pageToken = listResp.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            var item = new Google.Apis.YouTube.v3.Data.PlaylistItem
            {
                Snippet = new Google.Apis.YouTube.v3.Data.PlaylistItemSnippet
                {
                    PlaylistId = playlistId,
                    ResourceId = new Google.Apis.YouTube.v3.Data.ResourceId
                    {
                        Kind = "youtube#video",
                        VideoId = videoId
                    }
                }
            };

            var insert = _youtubeService.PlaylistItems.Insert(item, "snippet");
            var created = await insert.ExecuteAsync(cancellationToken);
            _logger.LogInformation("[YouTube] Added video {VideoId} to playlist {PlaylistId}", videoId, playlistId);
            return !string.IsNullOrWhiteSpace(created.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[YouTube] Failed to add video {VideoId} to playlist {PlaylistId}", videoId, playlistId);
            return false;
        }
    }

    /// Try to extract a base printer URI (scheme + host) from the configured Stream:Source URL.
    /// Returns a Uri pointing at the printer host with port 7125 (Moonraker default) when possible.
    /// </summary>
    private static Uri? GetPrinterBaseUriFromStreamSource(string source)
    {
    try
    {
            // If source is a full URL, parse it and replace the port with 7125
            if (Uri.TryCreate(source, UriKind.Absolute, out var srcUri))
            {
                var builder = new UriBuilder(srcUri)
                {
                    Port = 7125,
                    Path = string.Empty,
                    Query = string.Empty
                };
                return builder.Uri;
            }

            // Fallback: try to interpret as host or host:port
            if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var host = source.Split('/')[0];
                if (!host.Contains(":")) host = host + ":7125";
                if (Uri.TryCreate("http://" + host, UriKind.Absolute, out var u)) return u;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Best-effort fetch of the current print filename from a Moonraker instance at the given baseUri.
    /// Tries a few common endpoints and JSON paths, returns null if not found or on error.
    /// </summary>
    private static async Task<string?> GetMoonrakerFilenameAsync(Uri baseUri, CancellationToken cancellationToken = default)
    {
        // Use a shared HttpClient per call (short-lived is acceptable here since calls are infrequent)
        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(5) };

        // Candidate endpoints and JSON selectors
        var candidates = new[]
        {
            "/printer/objects/query?select=job",
            "/printer/objects/query?select=print_stats",
            "/printer/printerinfo",
            "/printer/objects/query?select=display_status",
            "/server/objects"
        };

        foreach (var ep in candidates)
        {
            try
            {
                var resp = await http.GetAsync(ep, cancellationToken);
                if (!resp.IsSuccessStatusCode) continue;
                var text = await resp.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(text)) continue;
                try
                {
                    var node = JsonNode.Parse(text);
                    if (node == null) continue;

                    // Common Moonraker response shapes: { "result": { "job": { "file": { "name": "foo.gcode" }}}}
                    // or { "result": { "status": { "filename": "foo.gcode" }}}
                    // Search recursively for likely fields
                    var name = FindFilenameInJson(node);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
                catch { continue; }
            }
            catch { continue; }
        }

        return null;
    }

    private static string? FindFilenameInJson(JsonNode? node)
    {
        if (node == null) return null;
        // If node is an object, check properties
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                var key = kv.Key?.ToLowerInvariant() ?? string.Empty;
                if (key.Contains("file") || key.Contains("filename") || key.Contains("job"))
                {
                    // Try to find a nested name or path
                    var maybe = ExtractNameFromNode(kv.Value);
                    if (!string.IsNullOrWhiteSpace(maybe)) return maybe;
                }

                // Recurse
                var rec = FindFilenameInJson(kv.Value);
                if (!string.IsNullOrWhiteSpace(rec)) return rec;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var it in arr)
            {
                var rec = FindFilenameInJson(it);
                if (!string.IsNullOrWhiteSpace(rec)) return rec;
            }
        }
        else
        {
            // Primitive
            var s = node.ToString();
            if (!string.IsNullOrWhiteSpace(s) && (s.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase) || s.IndexOf('.') > 0 && s.Length < 200))
            {
                return s;
            }
        }
        return null;
    }

    private static string? ExtractNameFromNode(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonObject o)
        {
            // Common property names
            if (o.TryGetPropertyValue("name", out var n) && n != null) return n.ToString();
            if (o.TryGetPropertyValue("filename", out var f) && f != null) return f.ToString();
            if (o.TryGetPropertyValue("path", out var p) && p != null) return p.ToString();
            if (o.TryGetPropertyValue("display_name", out var d) && d != null) return d.ToString();
        }
        else if (node is JsonValue v)
        {
            var s = v.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }

    /// <summary>
    /// Authenticate with YouTube using user OAuth (in-memory tokens only).
    /// Optionally seed a refresh token for headless operation via configuration.
    /// </summary>
    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var scopes = new[] { Google.Apis.YouTube.v3.YouTubeService.Scope.Youtube };

            var clientId = _config["YouTube:OAuth:ClientId"];
            var clientSecret = _config["YouTube:OAuth:ClientSecret"];
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogError("Error: YouTube OAuth ClientId and ClientSecret are required for user OAuth.");
                return false;
            }

            var secrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };

            // Use a single-file token store pointing at a mounted tokens path by default.
            // Preferred location: alongside the configured client secrets file, or explicit YouTube:OAuth:TokenFile.
            // Fallback to /app/data/tokens/youtube_token.json (or CWD/data/tokens/... in non-container runs).
            string? tokenFilePath = _config["YouTube:OAuth:TokenFile"];
            if (string.IsNullOrWhiteSpace(tokenFilePath))
            {
                var csPath = _config["YouTube:OAuth:ClientSecretsFilePath"];
                if (!string.IsNullOrWhiteSpace(csPath))
                {
                    var fullCs = Path.IsPathRooted(csPath) ? csPath : Path.Combine(Directory.GetCurrentDirectory(), csPath);
                    var dir = Path.GetDirectoryName(fullCs);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        tokenFilePath = Path.Combine(dir, "youtube_token.json");
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(tokenFilePath))
            {
                // Common persisted data path in container
                var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
                tokenFilePath = Path.Combine(dataDir, "tokens", "youtube_token.json");
            }
            var fileStore = new YoutubeTokenFileDataStore(tokenFilePath);
            IDataStore dataStore = fileStore;

            // If a refresh token is provided in configuration, seed it into youtube_token.json
            var refreshToken = _config["YouTube:OAuth:RefreshToken"];
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                _logger.LogInformation("[YouTube] Seeding configured refresh token into youtube_token.json (headless)");
                await dataStore.StoreAsync("user", new TokenResponse { RefreshToken = refreshToken });
            }

            // Also allow loading a full token response from youtube_token.json (read via our store).
            TokenResponse? importedToken = await dataStore.GetAsync<TokenResponse>("user");

            // Check if a token already exists in the store (youtube_token.json). If so, use it and skip interactive flows.
            var existingToken = importedToken;
            if (existingToken != null && !string.IsNullOrWhiteSpace(existingToken.RefreshToken))
            {
                var flow = new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow(
                    new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow.Initializer
                    {
                        ClientSecrets = secrets,
                        Scopes = scopes
                        , DataStore = dataStore
                    });
                _credential = new UserCredential(flow, "user", existingToken);
            }
            else
            {
                // No stored token, proceed with interactive or manual flow
                try
                {
                    if (TryLaunchBrowserPlaceholder())
                    {
                        _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                            secrets, scopes, "user", cancellationToken, dataStore);
                    }
                    else
                    {
                        _logger.LogWarning("Automatic browser launch appears unavailable. Falling back to manual auth flow.");

                        var requestUrl = new AuthorizationCodeRequestUrl(new Uri(Google.Apis.Auth.OAuth2.GoogleAuthConsts.AuthorizationUrl))
                        {
                            ClientId = clientId,
                            Scope = string.Join(" ", scopes),
                            RedirectUri = "urn:ietf:wg:oauth:2.0:oob",
                            ResponseType = "code"
                        };

                        _logger.LogWarning("Open the following URL in a browser and paste the resulting code here:");
                        _logger.LogWarning(requestUrl.Build().ToString());
                        _logger.LogWarning("Tip: If your terminal input is not working, create /app/data/youtube_oauth_code.txt with the code.");
                        Console.Write("Enter authorization code: ");
                        // Allow providing the auth code via config/env/file to support non-interactive shells
                        var preProvided = _config["YouTube:OAuth:AuthCode"];
                        if (string.IsNullOrWhiteSpace(preProvided)) preProvided = Environment.GetEnvironmentVariable("YOUTUBE_OAUTH_CODE");
                        if (string.IsNullOrWhiteSpace(preProvided))
                        {
                            var authCodeFile = _config["YouTube:OAuth:AuthCodeFile"];
                            if (!string.IsNullOrWhiteSpace(authCodeFile) && File.Exists(authCodeFile))
                            {
                                try { preProvided = await File.ReadAllTextAsync(authCodeFile, cancellationToken); }
                                catch { preProvided = null; }
                            }
                        }

                        string? code;
                        if (!string.IsNullOrWhiteSpace(preProvided))
                        {
                            code = preProvided.Trim();
                            _logger.LogInformation("(Using pre-provided auth code from config/env/file)");
                        }
                        else
                        {
                            code = await ReadAuthCodeInteractiveOrFileAsync(_config, cancellationToken);
                        }
                        if (string.IsNullOrWhiteSpace(code))
                        {
                            _logger.LogWarning("No code provided, aborting auth.");
                            return false;
                        }

                        var flow = new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow(
                            new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow.Initializer
                            {
                                ClientSecrets = secrets,
                                Scopes = scopes
                            });

                        var token = await flow.ExchangeCodeForTokenAsync("user", code, "urn:ietf:wg:oauth:2.0:oob", cancellationToken);
                        // Persist token to youtube_token.json so future runs can be headless
                        await dataStore.StoreAsync("user", token);
                        _credential = new UserCredential(flow, "user", token);
                    }
                }
                catch (Exception exAuth)
                {
                    _logger.LogWarning(exAuth, "Authentication flow failed: {Message}", exAuth.Message);
                    _logger.LogInformation("Attempting to load existing token from persistent store (if any)...");
                    // Try to read an existing token directly from the persistent store
                    var existing = await dataStore.GetAsync<TokenResponse>("user");
                    if (existing != null && !string.IsNullOrWhiteSpace(existing.RefreshToken))
                    {
                        _logger.LogInformation("Found existing refresh token in persistent store; attempting to use it.");
                        // Build a flow and credential from the stored token
                        var flow = new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow(
                            new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow.Initializer
                            {
                                ClientSecrets = secrets,
                                Scopes = scopes
                                , DataStore = dataStore
                            });

                        _credential = new UserCredential(flow, "user", existing);
                    }
                    else
                    {
                        _logger.LogWarning("No usable token in persistent store.");
                        return false;
                    }
                }
            }

            // Verify access token works (forces refresh if needed). If refresh is rejected because the
            // stored refresh token was issued to a different client (unauthorized_client), fall back
            // to using the raw access_token from the stored TokenResponse (no refresh).
            try
            {
                var tokStr = await _credential!.GetAccessTokenForRequestAsync(null, cancellationToken);
                if (string.IsNullOrEmpty(tokStr))
                {
                    _logger.LogError("[YouTube] Failed to obtain access token.");
                    return false;
                }
                _youtubeService = new Google.Apis.YouTube.v3.YouTubeService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = _credential,
                    ApplicationName = "PrintStreamer"
                });
                // Start background token refresh loop
                StartTokenRefreshLoop();
                return true;
            }
            catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException trex)
            {
                // If refresh was rejected due to unauthorized_client, but we have an access token available
                // in the stored TokenResponse, use it directly via GoogleCredential.FromAccessToken so we
                // don't prompt the user. Note this is a no-refresh credential and will stop working when
                // the access token expires.
                if (trex.Message != null && trex.Message.Contains("unauthorized_client") && _credential?.Token?.AccessToken != null)
                {
                    _logger.LogWarning("[YouTube] Refresh rejected (unauthorized_client). Using provided access_token without refresh.");
                    var accessOnly = Google.Apis.Auth.OAuth2.GoogleCredential.FromAccessToken(_credential.Token.AccessToken);
                    _youtubeService = new Google.Apis.YouTube.v3.YouTubeService(new BaseClientService.Initializer
                    {
                        HttpClientInitializer = accessOnly,
                        ApplicationName = "PrintStreamer"
                    });
                    _logger.LogInformation("[YouTube] Authentication successful (access_token only). Note: token will not be refreshed.");
                    return true;
                }
                // otherwise rethrow to be handled by outer catch
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication failed: {Message}", ex.Message);
            return false;
        }
    }

    private static async Task<string?> ReadAuthCodeInteractiveOrFileAsync(IConfiguration config, CancellationToken cancellationToken)
    {
        // Start a background read from Console.ReadLine, which may or may not work depending on TTY
        var consoleTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _ = Task.Run(() =>
            {
                try { consoleTcs.TrySetResult(Console.ReadLine()); }
                catch (Exception ex) { consoleTcs.TrySetException(ex); }
            });
        }
        catch { /* ignore spawning errors */ }

        // Determine candidate files to watch
        var candidates = new List<string>();
        var cfgFile = config["YouTube:OAuth:AuthCodeFile"];
        if (!string.IsNullOrWhiteSpace(cfgFile)) candidates.Add(cfgFile);
        try
        {
            var defaultPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "youtube_oauth_code.txt");
            candidates.Add(defaultPath);
        }
        catch { }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timeout = TimeSpan.FromMinutes(5);
        while (!cancellationToken.IsCancellationRequested && sw.Elapsed < timeout)
        {
            // Prefer console if it completed
            if (consoleTcs.Task.IsCompleted)
            {
                try { return (await consoleTcs.Task) ?? string.Empty; }
                catch { /* fall through to file polling */ }
            }

            // Poll files
            foreach (var path in candidates)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        var text = await File.ReadAllTextAsync(path, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text.Trim();
                        }
                    }
                }
                catch { }
            }

            try { await Task.Delay(500, cancellationToken); } catch { }
        }

        // Last chance: if console eventually produced something, return it
        if (consoleTcs.Task.IsCompleted)
        {
            try { return (await consoleTcs.Task) ?? string.Empty; } catch { }
        }
        return null;
    }

    /// <summary>
    /// Simple in-memory IDataStore implementation that keeps tokens in a concurrent dictionary
    /// for the lifetime of the process only.
    /// </summary>
    internal class InMemoryDataStore : IDataStore
    {
        private readonly ConcurrentDictionary<string, string> _store = new ConcurrentDictionary<string, string>();

        public Task ClearAsync()
        {
            _store.Clear();
            return Task.CompletedTask;
        }

        public Task DeleteAsync<stringT>(string key)
        {
            _store.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<T> GetAsync<T>(string key)
        {
            if (_store.TryGetValue(key, out var json))
            {
                var obj = JsonSerializer.Deserialize<T>(json);
                return Task.FromResult(obj!);
            }
            return Task.FromResult(default(T)!);
        }

        public Task StoreAsync<T>(string key, T value)
        {
            var json = JsonSerializer.Serialize(value);
            _store[key] = json;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// IDataStore implementation that persists a single TokenResponse to a JSON file.
    /// This keeps refresh tokens and access tokens only in `youtube_token.json` as requested.
    /// </summary>
    internal class YoutubeTokenFileDataStore : IDataStore
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public YoutubeTokenFileDataStore(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        public async Task ClearAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (File.Exists(_filePath)) File.Delete(_filePath);
            }
            finally { _lock.Release(); }
        }

        public async Task DeleteAsync<stringT>(string key)
        {
            // Single-token store: clearing the file is equivalent to deleting the key
            await ClearAsync();
        }

        public async Task<T> GetAsync<T>(string key)
        {
            await _lock.WaitAsync();
            try
            {
                if (!File.Exists(_filePath)) return default!;
                var json = await File.ReadAllTextAsync(_filePath);
                // If the requested type is TokenResponse, map possible snake_case fields
                if (typeof(T) == typeof(TokenResponse))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var token = new TokenResponse();
                        if (root.TryGetProperty("access_token", out var at)) token.AccessToken = at.GetString();
                        if (root.TryGetProperty("refresh_token", out var rt)) token.RefreshToken = rt.GetString();
                        if (root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number) token.ExpiresInSeconds = ei.GetInt32();
                        if (root.TryGetProperty("scope", out var sc)) token.Scope = sc.GetString();
                        if (root.TryGetProperty("token_type", out var tt)) token.TokenType = tt.GetString();
                        return (T)(object)token;
                    }
                    catch
                    {
                        return default!;
                    }
                }

                // Fallback: try to deserialize generically
                return JsonSerializer.Deserialize<T>(json)!;
            }
            finally { _lock.Release(); }
        }

        public async Task StoreAsync<T>(string key, T value)
        {
            await _lock.WaitAsync();
            try
            {
                // For TokenResponse, serialize fields with snake_case names for compatibility with the existing token file format
                if (value is TokenResponse tr)
                {
                    var obj = new
                    {
                        access_token = tr.AccessToken,
                        expires_in = tr.ExpiresInSeconds,
                        refresh_token = tr.RefreshToken,
                        scope = tr.Scope,
                        token_type = tr.TokenType
                    };
                    var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
                    // atomic write
                    var tmp = _filePath + ".tmp";
                    var dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                    await File.WriteAllTextAsync(tmp, json);
                    File.Move(tmp, _filePath, overwrite: true);
                    return;
                }

                // Generic fallback
                var generic = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
                var tmp2 = _filePath + ".tmp";
                var dir2 = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(dir2)) Directory.CreateDirectory(dir2);
                await File.WriteAllTextAsync(tmp2, generic);
                File.Move(tmp2, _filePath, overwrite: true);
            }
            finally { _lock.Release(); }
        }
    }

    private void StartTokenRefreshLoop()
    {
        // If already started, ignore
        if (_refreshTask != null) return;

        // Start the refresh loop for user credentials.

        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        _refreshTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (_credential == null)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), ct);
                            continue;
                        }

                        // Determine wait interval based on token expiry
                        var expiresIn = _credential.Token?.ExpiresInSeconds ?? 0;
                        // If expiry info is not available, poll every 5 minutes
                        TimeSpan wait = expiresIn > 60 ? TimeSpan.FromSeconds(Math.Max(30, expiresIn / 2)) : TimeSpan.FromMinutes(5);

                        // Wait until next refresh window or cancellation
                        await Task.Delay(wait, ct);

                        if (ct.IsCancellationRequested) break;

                        // Force a token refresh by requesting an access token (the library will refresh if needed)
                        try
                        {
                            var token = await _credential.GetAccessTokenForRequestAsync(null, ct);
                            _logger.LogInformation("Refreshed access token at {Time} (len={Len})", DateTime.UtcNow, token?.Length);
                        }
                        catch (Exception rex)
                        {
                            _logger.LogWarning(rex, "Warning: failed to refresh access token: {Message}", rex.Message);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Token refresh loop error: {Message}", ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    /// <summary>
    /// Best-effort check to see if a system browser can be launched. Returns true when xdg-open exists and appears runnable.
    /// This does NOT guarantee the browser will open the OAuth URL, but helps decide whether to use the automatic flow.
    /// </summary>
    private bool TryLaunchBrowserPlaceholder()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = "about:blank",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            // wait briefly
            if (!p.WaitForExit(500))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Create a YouTube live broadcast and stream, bind them together, and return the RTMP ingestion URL.
    /// </summary>
    public async Task<(string? rtmpUrl, string? streamKey, string? broadcastId, string? filename)> CreateLiveBroadcastAsync(CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            _logger.LogError("Error: Not authenticated. Call AuthenticateAsync first.");
            return (null, null, null, null);
        }

        try
        {
            // Read broadcast settings from config
            var title = _config["YouTube:LiveBroadcast:Title"] ?? "Print Streamer Live";
            var description = _config["YouTube:LiveBroadcast:Description"] ?? "Live stream from 3D printer";
            var privacy = _config["YouTube:LiveBroadcast:Privacy"] ?? "unlisted";
            var categoryId = _config["YouTube:LiveBroadcast:CategoryId"] ?? "28";

            // Try to get Moonraker info to augment title and description
            string? moonrakerFilename = null;
            MoonrakerClient.MoonrakerPrintInfo? moonrakerInfo = null;

            // Build a description that references PrintStreamer and includes print details
            var appAttribution = "Streamed with PrintStreamer — https://github.com/kainazzzo/printstreamer";
            if (!string.IsNullOrWhiteSpace(description))
            {
                description = description.TrimEnd() + "\n\n" + appAttribution;
            }
            else
            {
                description = appAttribution;
            }

            // Try to augment the title with the currently printing file name from Moonraker and collect details for description
            try
            {
                // Allow explicit Moonraker base URL in config (e.g. http://192.168.1.117:7125/)
                var moonBase = _config["Moonraker:BaseUrl"];
                Uri? baseUri = null;
                if (!string.IsNullOrWhiteSpace(moonBase) && Uri.TryCreate(moonBase, UriKind.Absolute, out var cfgUri))
                {
                    baseUri = cfgUri;
                }
                else
                {
                    // Derive printer base host from Stream:Source if possible (expecting http://<ip>/...)
                    var src = _config["Stream:Source"];
                    if (!string.IsNullOrWhiteSpace(src)) baseUri = _moonrakerClient.GetPrinterBaseUriFromStreamSource(src);
                }

                if (baseUri != null)
                {
                    var apiKey = _config["Moonraker:ApiKey"];
                    var authHeader = _config["Moonraker:AuthHeader"]; // e.g. X-Api-Key or Authorization
                    var info = await _moonrakerClient.GetPrintInfoAsync(baseUri, apiKey, authHeader, cancellationToken);
                    moonrakerInfo = info;

                    if (info != null && !string.IsNullOrWhiteSpace(info.Filename))
                    {
                        moonrakerFilename = info.Filename;
                        var cleanFilename = System.IO.Path.GetFileNameWithoutExtension(info.Filename);
                        if (!string.IsNullOrWhiteSpace(cleanFilename))
                        {
                            title = $"{title} - {cleanFilename}";
                        }
                    }

                    // Append details (temps + filament) into the description, if available
                    if (info != null)
                    {
                        var detailsList = new System.Collections.Generic.List<string>();
                        if (info.BedTempActual.HasValue || info.BedTempTarget.HasValue)
                            detailsList.Add($"Bed: {info.BedTempActual?.ToString("F1") ?? "n/a"}°C / {info.BedTempTarget?.ToString("F1") ?? "n/a"}°C");
                        if (info.Tool0Temp.HasValue)
                        {
                            var t = info.Tool0Temp.Value;
                            detailsList.Add($"Nozzle: {t.Actual?.ToString("F1") ?? "n/a"}°C / {t.Target?.ToString("F1") ?? "n/a"}°C");
                        }
                        if (!string.IsNullOrWhiteSpace(info.FilamentType) || !string.IsNullOrWhiteSpace(info.FilamentColor) || !string.IsNullOrWhiteSpace(info.FilamentBrand))
                        {
                            var fil = $"Filament: {info.FilamentBrand ?? ""} {info.FilamentType ?? ""} {info.FilamentColor ?? ""}".Trim();
                            detailsList.Add(fil);
                        }
                        if (info.FilamentUsedMm.HasValue || info.FilamentTotalMm.HasValue)
                        {
                            detailsList.Add($"Filament Used: {info.FilamentUsedMm?.ToString("F0") ?? "n/a"}mm / {info.FilamentTotalMm?.ToString("F0") ?? "n/a"}mm");
                        }
                        if (detailsList.Count > 0)
                        {
                            description += "\n\n" + string.Join("\n", detailsList);
                        }
                    }
                }
            }
            catch (Exception mx)
            {
                _logger.LogWarning(mx, "Warning: failed to query Moonraker for print filename: {Message}", mx.Message);
            }

            _logger.LogInformation("Creating YouTube live broadcast: {Title}", title);

            // 1. Create LiveBroadcast
            var broadcast = new LiveBroadcast
            {
                Snippet = new LiveBroadcastSnippet
                {
                    Title = title,
                    Description = description,
                    ScheduledStartTimeDateTimeOffset = DateTimeOffset.UtcNow
                },
                Status = new LiveBroadcastStatus
                {
                    PrivacyStatus = privacy,
                    SelfDeclaredMadeForKids = false
                },
                ContentDetails = new LiveBroadcastContentDetails
                {
                    EnableAutoStart = true,
                    EnableAutoStop = true
                }
            };

            var broadcastRequest = _youtubeService.LiveBroadcasts.Insert(broadcast, "snippet,status,contentDetails");
            var createdBroadcast = await broadcastRequest.ExecuteAsync(cancellationToken);

            _logger.LogInformation("Broadcast created with ID: {Id}", createdBroadcast.Id);

            // 2. Create LiveStream
            var stream = new LiveStream
            {
                Snippet = new LiveStreamSnippet
                {
                    Title = title,
                    Description = description
                },
                Cdn = new CdnSettings
                {
                    FrameRate = "variable",
                    IngestionType = "rtmp",
                    Resolution = "variable"
                },
                ContentDetails = new LiveStreamContentDetails
                {
                    IsReusable = false
                }
            };

            var streamRequest = _youtubeService.LiveStreams.Insert(stream, "snippet,cdn,contentDetails");
            var createdStream = await streamRequest.ExecuteAsync(cancellationToken);

            _logger.LogInformation("Stream created with ID: {Id}", createdStream.Id);

            // 3. Bind the stream to the broadcast
            var bindRequest = _youtubeService.LiveBroadcasts.Bind(createdBroadcast.Id, "id,contentDetails");
            bindRequest.StreamId = createdStream.Id;
            var boundBroadcast = await bindRequest.ExecuteAsync(cancellationToken);

            _logger.LogInformation("Stream bound to broadcast.");

            // 4. Extract RTMP ingestion info
            var rtmpUrl = createdStream.Cdn.IngestionInfo.IngestionAddress;
            var streamKey = createdStream.Cdn.IngestionInfo.StreamName;
            var streamId = createdStream.Id;

            _logger.LogInformation("RTMP URL: {Rtmp}", rtmpUrl);
            _logger.LogInformation("Stream Key: {Key}", streamKey);
            _logger.LogInformation("Broadcast URL: https://www.youtube.com/watch?v={BroadcastId}", createdBroadcast.Id);

            // Return streamId in tuple via broadcastId position isn't ideal; for now we return broadcastId and maintain streamId in the created stream object.
            // Caller can fetch streamId via createdStream.Id if needed. We'll also store last created stream id in a field if debugging required.
            _lastCreatedStreamId = streamId;
            return (rtmpUrl, streamKey, createdBroadcast.Id, moonrakerFilename);
        }
        catch (Google.GoogleApiException gae)
        {
            _logger.LogError(gae, "Failed to create live broadcast: {Message}", gae.Message);
            _logger.LogError("HTTP Status: {Status}", gae.HttpStatusCode);
            if (gae.Error != null)
            {
                _logger.LogError("Google API error message: {Msg}", gae.Error.Message);
                if (gae.Error.Errors != null)
                {
                    _logger.LogError("Details:");
                    foreach (var e in gae.Error.Errors)
                    {
                        _logger.LogError(" - {Domain}/{Reason}: {Msg}", e.Domain, e.Reason, e.Message);
                    }
                }
            }
            else
            {
                _logger.LogError(gae.ToString());
            }
            return (null, null, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create live broadcast: {Message}", ex.Message);
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// Transition the broadcast to "live" status (starts the stream).
    /// </summary>
    public async Task<bool> TransitionBroadcastToLiveAsync(string broadcastId, CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            _logger.LogError("Error: Not authenticated.");
            return false;
        }

        try
        {
            _logger.LogInformation("Transitioning broadcast {BroadcastId} to live...", broadcastId);

            var transitionRequest = _youtubeService.LiveBroadcasts.Transition(
                LiveBroadcastsResource.TransitionRequest.BroadcastStatusEnum.Live,
                broadcastId,
                "id,status"
            );

            var result = await transitionRequest.ExecuteAsync(cancellationToken);
            _logger.LogInformation("Broadcast is now live! Status: {Status}", result.Status.LifeCycleStatus);
            return true;
        }
        catch (Google.GoogleApiException gae)
        {
            _logger.LogError(gae, "Failed to transition broadcast to live: {Message}", gae.Message);
            _logger.LogError("HTTP Status: {Status}", gae.HttpStatusCode);
            if (gae.Error != null)
            {
                _logger.LogError("Google API error message: {Msg}", gae.Error.Message);
                if (gae.Error.Errors != null)
                {
                    _logger.LogError("Details:");
                    foreach (var e in gae.Error.Errors)
                    {
                        _logger.LogError(" - {Domain}/{Reason}: {Msg}", e.Domain, e.Reason, e.Message);
                    }
                }
            }
            else
            {
                _logger.LogError(gae.ToString());
            }
            return false;
        }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to transition broadcast to live: {Message}", ex.Message);
                return false;
            }
    }

    private string? _lastCreatedStreamId;

    /// <summary>
    /// Poll the LiveStream ingestion status until it's "active" or timeout.
    /// </summary>
    public async Task<bool> WaitForIngestionAsync(string? streamId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            _logger.LogWarning("YouTube service not initialized.");
            return false;
        }

        if (string.IsNullOrEmpty(streamId)) streamId = _lastCreatedStreamId;
        if (string.IsNullOrEmpty(streamId))
        {
            _logger.LogWarning("No streamId available to poll ingestion status.");
            return false;
        }

        timeout ??= TimeSpan.FromSeconds(30);

        // Use polling manager if available and enabled
        if (_pollingManager != null)
        {
            _logger.LogInformation("Polling for ingestion status using YouTubePollingManager: streamId={StreamId}, timeout={TimeoutSec}s",
                streamId, timeout.Value.TotalSeconds);

            var result = await _pollingManager.PollUntilConditionAsync(
                fetchFunc: async () =>
                {
                    var req = _youtubeService.LiveStreams.List("id,cdn,status");
                    req.Id = streamId;
                    var resp = await _pollingManager.ExecuteWithRateLimitAsync(
                        async () => await req.ExecuteAsync(cancellationToken),
                        $"stream-status:{streamId}",
                        cancellationToken
                    );
                    return resp?.Items?.FirstOrDefault();
                },
                condition: (stream) =>
                {
                    var status = stream?.Status?.StreamStatus;
                    _logger.LogDebug("Stream status poll: streamId={StreamId}, status={Status}", streamId, status);
                    return string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);
                },
                timeout: timeout.Value,
                context: $"ingestion:{streamId}",
                cancellationToken: cancellationToken
            );

            if (result != null && string.Equals(result.Status?.StreamStatus, "active", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Ingestion is active.");
                return true;
            }

            _logger.LogWarning("Ingestion did not become active within timeout.");
            return false;
        }

        // Fallback: original polling logic (when manager disabled)
        var deadline = DateTime.UtcNow + timeout.Value;
        try
        {
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                var req = _youtubeService.LiveStreams.List("id,cdn,status");
                req.Id = streamId;
                var resp = await req.ExecuteAsync(cancellationToken);
                if (resp.Items != null && resp.Items.Count > 0)
                {
                    var s = resp.Items[0];
                    var status = s.Status?.StreamStatus;
                    _logger.LogInformation("Polled stream status: streamId={StreamId} status={Status}", streamId, status);
                    // Log the full LiveStream object for diagnosis
                    try
                    {
                        _logger.LogDebug("LiveStream poll response:\n{Payload}", JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch { }
                    if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Ingestion is active.");
                        return true;
                    }
                }
                await Task.Delay(2000, cancellationToken);
            }
            _logger.LogWarning("Ingestion did not become active within timeout.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while polling ingestion status: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// End the broadcast (transition to "complete").
    /// </summary>
    public async Task<bool> EndBroadcastAsync(string broadcastId, CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            _logger.LogError("Error: Not authenticated.");
            return false;
        }

        try
        {
            _logger.LogInformation("Ending broadcast {BroadcastId}...", broadcastId);

            var transitionRequest = _youtubeService.LiveBroadcasts.Transition(
                LiveBroadcastsResource.TransitionRequest.BroadcastStatusEnum.Complete,
                broadcastId,
                "id,status"
            );

            var result = await transitionRequest.ExecuteAsync(cancellationToken);
            _logger.LogInformation("Broadcast ended. Status: {Status}", result.Status.LifeCycleStatus);
            return true;
        }
        catch (Google.GoogleApiException gae)
        {
            _logger.LogError(gae, "Failed to end broadcast: {Message}", gae.Message);
            _logger.LogError("HTTP Status: {Status}", gae.HttpStatusCode);
            if (gae.Error != null)
            {
                _logger.LogError("Google API error message: {Msg}", gae.Error.Message);
                if (gae.Error.Errors != null)
                {
                    _logger.LogError("Details:");
                    foreach (var e in gae.Error.Errors)
                    {
                        _logger.LogError(" - {Domain}/{Reason}: {Msg}", e.Domain, e.Reason, e.Message);
                    }
                }
            }
            else
            {
                _logger.LogError(gae.ToString());
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to end broadcast: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Update the privacy status of a live broadcast (public, unlisted, or private).
    /// </summary>
    public async Task<bool> UpdateBroadcastPrivacyAsync(string broadcastId, string privacyStatus, CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            _logger.LogError("Error: Not authenticated.");
            return false;
        }

        // Validate privacy status
        var validStatuses = new[] { "public", "unlisted", "private" };
        if (!validStatuses.Contains(privacyStatus.ToLowerInvariant()))
        {
            _logger.LogWarning("Invalid privacy status: {Privacy}. Must be one of: {Valid}", privacyStatus, string.Join(", ", validStatuses));
            return false;
        }

        try
        {
            _logger.LogInformation("Updating broadcast {BroadcastId} privacy to {Privacy}...", broadcastId, privacyStatus);

            // First, fetch the current broadcast to get all required fields
            var listRequest = _youtubeService.LiveBroadcasts.List("id,snippet,status,contentDetails");
            listRequest.Id = broadcastId;
            var listResponse = await listRequest.ExecuteAsync(cancellationToken);

            if (listResponse.Items == null || listResponse.Items.Count == 0)
            {
                _logger.LogWarning("Broadcast {BroadcastId} not found.", broadcastId);
                return false;
            }

            var broadcast = listResponse.Items[0];
            
            // Update the privacy status
            broadcast.Status.PrivacyStatus = privacyStatus.ToLowerInvariant();

            // Update the broadcast
            var updateRequest = _youtubeService.LiveBroadcasts.Update(broadcast, "id,snippet,status,contentDetails");
            var result = await updateRequest.ExecuteAsync(cancellationToken);

            _logger.LogInformation("Broadcast privacy updated to {Privacy}", result.Status.PrivacyStatus);
            return true;
        }
        catch (Google.GoogleApiException gae)
        {
            _logger.LogError(gae, "Failed to update broadcast privacy: {Message}", gae.Message);
            _logger.LogError("HTTP Status: {Status}", gae.HttpStatusCode);
            if (gae.Error != null)
            {
                _logger.LogError("Google API error message: {Msg}", gae.Error.Message);
                if (gae.Error.Errors != null)
                {
                    _logger.LogError("Details:");
                    foreach (var e in gae.Error.Errors)
                    {
                        _logger.LogError(" - {Domain}/{Reason}: {Msg}", e.Domain, e.Reason, e.Message);
                    }
                }
            }
            else
            {
                _logger.LogError(gae.ToString());
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update broadcast privacy: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Get the current privacy status of a live broadcast.
    /// </summary>
    public async Task<string?> GetBroadcastPrivacyAsync(string broadcastId, CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            _logger.LogError("Error: Not authenticated.");
            return null;
        }

        try
        {
            var listRequest = _youtubeService.LiveBroadcasts.List("id,status");
            listRequest.Id = broadcastId;
            var listResponse = await listRequest.ExecuteAsync(cancellationToken);

            if (listResponse.Items == null || listResponse.Items.Count == 0)
            {
                _logger.LogWarning("Broadcast {BroadcastId} not found.", broadcastId);
                return null;
            }

            return listResponse.Items[0].Status?.PrivacyStatus;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get broadcast privacy: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Wait for ingestion to be active and attempt to transition the broadcast to live with retries and diagnostics.
    /// </summary>
    public async Task<bool> TransitionBroadcastToLiveWhenReadyAsync(string broadcastId, TimeSpan? maxWait = null, int maxAttempts = 12, CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            _logger.LogError("Error: Not authenticated.");
            return false;
        }

        // Give YouTube more time to detect ingestion for variable sources/networks
        maxWait ??= TimeSpan.FromSeconds(180);

        // First wait for ingestion to become active (polling)
    _logger.LogInformation("Waiting up to {Seconds}s for ingestion to become active...", maxWait.Value.TotalSeconds);
        var ingestionOk = await WaitForIngestionAsync(null, maxWait, cancellationToken);

        if (!ingestionOk)
        {
            _logger.LogWarning("Ingestion did not report active status within timeout. Will attempt transition anyway but will retry on transient errors.");
        }

        // Attempt to transition with retries
        var attempt = 0;
        while (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                _logger.LogInformation("Attempt {Attempt} to transition broadcast {BroadcastId} to live...", attempt, broadcastId);
                
                // Check current broadcast lifecycle status first to avoid redundant transitions
                LiveBroadcast? current = null;
                if (_pollingManager != null)
                {
                    current = await _pollingManager.ExecuteWithRateLimitAsync(
                        async () =>
                        {
                            var listReq = _youtubeService.LiveBroadcasts.List("id,status");
                            listReq.Id = broadcastId;
                            var resp = await listReq.ExecuteAsync(cancellationToken);
                            return resp.Items?.FirstOrDefault();
                        },
                        $"broadcast-status:{broadcastId}",
                        cancellationToken
                    );
                }
                else
                {
                    // Fallback without polling manager
                    var listReq = _youtubeService.LiveBroadcasts.List("id,status");
                    listReq.Id = broadcastId;
                    var resp = await listReq.ExecuteAsync(cancellationToken);
                    current = resp.Items?.FirstOrDefault();
                }

                    if (current != null)
                    {
                        var life = current.Status?.LifeCycleStatus;
                        _logger.LogInformation("Current broadcast lifecycle: {Life}", life);
                        if (string.Equals(life, "live", StringComparison.OrdinalIgnoreCase) || 
                            string.Equals(life, "liveStarting", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("Broadcast already live/liveStarting; skipping transition.");
                            return true;
                        }
                    }

                // Execute transition
                var transitionRequest = _youtubeService.LiveBroadcasts.Transition(
                    LiveBroadcastsResource.TransitionRequest.BroadcastStatusEnum.Live,
                    broadcastId,
                    "id,status"
                );

                LiveBroadcast result;
                if (_pollingManager != null)
                {
                    result = await _pollingManager.ExecuteWithRateLimitAsync(
                        async () => await transitionRequest.ExecuteAsync(cancellationToken),
                        $"broadcast-transition:{broadcastId}:{attempt}",
                        cancellationToken
                    );
                }
                else
                {
                    result = await transitionRequest.ExecuteAsync(cancellationToken);
                }

                _logger.LogInformation("Transition succeeded: {Status}", result.Status.LifeCycleStatus);
                return true;
            }
            catch (Google.GoogleApiException gae)
            {
                _logger.LogWarning(gae, "Transition attempt {Attempt} failed: {Message}", attempt, gae.Message);
                _logger.LogWarning("HTTP Status: {Status}", gae.HttpStatusCode);

                // Log API error details if present
                if (gae.Error != null)
                {
                    _logger.LogWarning("Google API error message: {Msg}", gae.Error.Message);
                    if (gae.Error.Errors != null)
                    {
                        _logger.LogWarning("Details:");
                        foreach (var e in gae.Error.Errors)
                        {
                            _logger.LogWarning(" - {Domain}/{Reason}: {Msg}", e.Domain, e.Reason, e.Message);
                        }

                        // If this error is a redundant or invalid transition, consider it success
                        foreach (var e in gae.Error.Errors)
                        {
                            if (string.Equals(e.Reason, "redundantTransition", StringComparison.OrdinalIgnoreCase) || 
                                string.Equals(e.Reason, "invalidTransition", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogInformation("Received {Reason}; treating transition as success.", e.Reason);
                                return true;
                            }
                        }
                    }
                }

                // Dump diagnostics: LiveBroadcast and LiveStream
                try { await LogBroadcastAndStreamResourcesAsync(broadcastId, _lastCreatedStreamId, cancellationToken); } catch { }

                if (attempt < maxAttempts)
                {
                    // Use polling manager's backoff calculation if available
                    TimeSpan backoff;
                    if (_pollingManager != null)
                    {
                        backoff = _pollingManager.CalculateInterval(attempt + 1);
                    }
                    else
                    {
                        backoff = TimeSpan.FromSeconds(2 * attempt);
                    }

                    _logger.LogInformation("Retrying in {Seconds}s...", backoff.TotalSeconds);
                    await Task.Delay(backoff, cancellationToken);
                }
                else
                {
                    _logger.LogWarning("Max transition attempts reached; giving up on transitioning the broadcast.");
                    _logger.LogInformation("Note: the ffmpeg media push will continue, and viewers may still see the stream depending on YouTube settings. You can manually 'Go Live' or transition the broadcast in YouTube Studio if needed.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error trying to transition: {Message}", ex.Message);
                try { await LogBroadcastAndStreamResourcesAsync(broadcastId, null, CancellationToken.None); } catch { }
                return false;
            }
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            _refreshCts?.Cancel();
            _refreshTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch { }
        _youtubeService?.Dispose();
    }

    /// <summary>
    /// Fetch and print the full LiveBroadcast and LiveStream resources for debugging.
    /// </summary>
    public async Task LogBroadcastAndStreamResourcesAsync(string? broadcastId, string? streamId, CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            _logger.LogWarning("YouTube service not initialized.");
            return;
        }

        try
        {
            if (!string.IsNullOrEmpty(broadcastId))
            {
                var bReq = _youtubeService.LiveBroadcasts.List("id,snippet,status,contentDetails");
                bReq.Id = broadcastId;
                var bResp = await bReq.ExecuteAsync(cancellationToken);
                _logger.LogInformation("LiveBroadcast resource:");
                _logger.LogDebug(JsonSerializer.Serialize(bResp, new JsonSerializerOptions { WriteIndented = true }));
            }

            if (string.IsNullOrEmpty(streamId)) streamId = _lastCreatedStreamId;
            if (!string.IsNullOrEmpty(streamId))
            {
                var sReq = _youtubeService.LiveStreams.List("id,snippet,cdn,contentDetails,status");
                sReq.Id = streamId;
                var sResp = await sReq.ExecuteAsync(cancellationToken);
                _logger.LogInformation("LiveStream resource:");
                _logger.LogDebug(JsonSerializer.Serialize(sResp, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging broadcast/stream resources: {Message}", ex.Message);
        }
    }
}
}
