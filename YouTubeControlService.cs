using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
// Google.Apis.Util.Store is used for IDataStore implementations
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Util.Store;
// using Google.Apis.Auth.OAuth2.Flows; // not used with the standard broker flow
using System.Collections.Concurrent;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

internal class YouTubeControlService : IDisposable
{
    private readonly IConfiguration _config;
    private Google.Apis.YouTube.v3.YouTubeService? _youtubeService;
    private UserCredential? _credential; // Used for user OAuth flow
    private readonly string _tokenPath;
    private readonly InMemoryDataStore _inMemoryStore = new InMemoryDataStore();
    private Task? _refreshTask;
    private CancellationTokenSource? _refreshCts;

    public YouTubeControlService(IConfiguration config)
    {
        _config = config;
        _tokenPath = Path.Combine(Directory.GetCurrentDirectory(), "youtube_tokens");
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
                Console.WriteLine("Error: YouTube OAuth ClientId and ClientSecret are required for user OAuth.");
                return false;
            }

            Console.WriteLine("Authenticating with YouTube using user OAuth...");

            var secrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };

            // Use a single-file token store pointing at youtube_token.json so refresh tokens
            // are only stored in that file (user requested). This store will be used by the
            // Google OAuth flow to persist tokens on refresh as well.
            var tokenFilePath = Path.Combine(Directory.GetCurrentDirectory(), "youtube_token.json");
            var fileStore = new YoutubeTokenFileDataStore(tokenFilePath);
            IDataStore dataStore = fileStore;

            // If a refresh token is provided in configuration, seed it into youtube_token.json
            var refreshToken = _config["YouTube:OAuth:RefreshToken"];
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                Console.WriteLine("Seeding configured refresh token into youtube_token.json (headless)");
                await dataStore.StoreAsync("user", new TokenResponse { RefreshToken = refreshToken });
            }

            // Also allow loading a full token response from youtube_token.json (read via our store).
            TokenResponse? importedToken = await dataStore.GetAsync<TokenResponse>("user");
            if (importedToken != null)
            {
                Console.WriteLine("Found existing token in youtube_token.json and loaded it.");
            }

            // Check if a token already exists in the store (youtube_token.json). If so, use it and skip interactive flows.
            var existingToken = importedToken;
            if (existingToken != null && !string.IsNullOrWhiteSpace(existingToken.RefreshToken))
            {
                Console.WriteLine("Found existing token in persistent store; using it for authentication.");
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
                        Console.WriteLine("Automatic browser launch appears unavailable. Falling back to manual auth flow.");

                        var requestUrl = new AuthorizationCodeRequestUrl(new Uri(Google.Apis.Auth.OAuth2.GoogleAuthConsts.AuthorizationUrl))
                        {
                            ClientId = clientId,
                            Scope = string.Join(" ", scopes),
                            RedirectUri = "urn:ietf:wg:oauth:2.0:oob",
                            ResponseType = "code"
                        };

                        Console.WriteLine("Open the following URL in a browser and paste the resulting code here:");
                        Console.WriteLine(requestUrl.Build().ToString());
                        Console.Write("Enter authorization code: ");
                        var code = Console.ReadLine();
                        if (string.IsNullOrWhiteSpace(code))
                        {
                            Console.WriteLine("No code provided, aborting auth.");
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
                    Console.WriteLine($"Authentication flow failed: {exAuth.Message}");
                    Console.WriteLine("Attempting to load existing token from persistent store (if any)...");
                    // Try to read an existing token directly from the persistent store
                    var existing = await dataStore.GetAsync<TokenResponse>("user");
                    if (existing != null && !string.IsNullOrWhiteSpace(existing.RefreshToken))
                    {
                        Console.WriteLine("Found existing refresh token in persistent store; attempting to use it.");
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
                        Console.WriteLine("No usable token in persistent store.");
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
                    Console.WriteLine("Failed to obtain access token.");
                    return false;
                }
                Console.WriteLine("Authentication successful!");
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
                    Console.WriteLine("Refresh rejected (unauthorized_client). Using provided access_token without refresh.");
                    var accessOnly = Google.Apis.Auth.OAuth2.GoogleCredential.FromAccessToken(_credential.Token.AccessToken);
                    _youtubeService = new Google.Apis.YouTube.v3.YouTubeService(new BaseClientService.Initializer
                    {
                        HttpClientInitializer = accessOnly,
                        ApplicationName = "PrintStreamer"
                    });
                    Console.WriteLine("Authentication successful (access_token only). Note: token will not be refreshed.");
                    return true;
                }
                // otherwise rethrow to be handled by outer catch
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Authentication failed: {ex.Message}");
            Console.WriteLine(ex.ToString());
            return false;
        }
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
                    await File.WriteAllTextAsync(tmp, json);
                    File.Move(tmp, _filePath, overwrite: true);
                    return;
                }

                // Generic fallback
                var generic = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
                var tmp2 = _filePath + ".tmp";
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
                            Console.WriteLine($"Refreshed access token at {DateTime.UtcNow:O} (len={token?.Length})");
                        }
                        catch (Exception rex)
                        {
                            Console.WriteLine($"Warning: failed to refresh access token: {rex.Message}");
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Token refresh loop error: {ex.Message}");
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
    public async Task<(string? rtmpUrl, string? streamKey, string? broadcastId)> CreateLiveBroadcastAsync(CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            Console.WriteLine("Error: Not authenticated. Call AuthenticateAsync first.");
            return (null, null, null);
        }

        try
        {
            // Read broadcast settings from config
            var title = _config["YouTube:LiveBroadcast:Title"] ?? "Print Streamer Live";
            var description = _config["YouTube:LiveBroadcast:Description"] ?? "Live stream from 3D printer";
            var privacy = _config["YouTube:LiveBroadcast:Privacy"] ?? "unlisted";
            var categoryId = _config["YouTube:LiveBroadcast:CategoryId"] ?? "28";

            var streamTitle = _config["YouTube:LiveStream:Title"] ?? "Print Stream";
            var streamDescription = _config["YouTube:LiveStream:Description"] ?? "3D printer camera feed";

            Console.WriteLine($"Creating YouTube live broadcast: {title}");

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

            Console.WriteLine($"Broadcast created with ID: {createdBroadcast.Id}");

            // 2. Create LiveStream
            var stream = new LiveStream
            {
                Snippet = new LiveStreamSnippet
                {
                    Title = streamTitle,
                    Description = streamDescription
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

            Console.WriteLine($"Stream created with ID: {createdStream.Id}");

            // 3. Bind the stream to the broadcast
            var bindRequest = _youtubeService.LiveBroadcasts.Bind(createdBroadcast.Id, "id,contentDetails");
            bindRequest.StreamId = createdStream.Id;
            var boundBroadcast = await bindRequest.ExecuteAsync(cancellationToken);

            Console.WriteLine("Stream bound to broadcast.");

            // 4. Extract RTMP ingestion info
            var rtmpUrl = createdStream.Cdn.IngestionInfo.IngestionAddress;
            var streamKey = createdStream.Cdn.IngestionInfo.StreamName;
            var streamId = createdStream.Id;

            Console.WriteLine($"RTMP URL: {rtmpUrl}");
            Console.WriteLine($"Stream Key: {streamKey}");
            Console.WriteLine($"Broadcast URL: https://www.youtube.com/watch?v={createdBroadcast.Id}");

            // Return streamId in tuple via broadcastId position isn't ideal; for now we return broadcastId and maintain streamId in the created stream object.
            // Caller can fetch streamId via createdStream.Id if needed. We'll also store last created stream id in a field if debugging required.
            _lastCreatedStreamId = streamId;
            return (rtmpUrl, streamKey, createdBroadcast.Id);
        }
        catch (Google.GoogleApiException gae)
        {
            Console.WriteLine($"Failed to create live broadcast: {gae.Message}");
            Console.WriteLine($"HTTP Status: {gae.HttpStatusCode}");
            if (gae.Error != null)
            {
                Console.WriteLine($"Google API error message: {gae.Error.Message}");
                if (gae.Error.Errors != null)
                {
                    Console.WriteLine("Details:");
                    foreach (var e in gae.Error.Errors)
                    {
                        Console.WriteLine($" - {e.Domain}/{e.Reason}: {e.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine(gae.ToString());
            }
            return (null, null, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create live broadcast: {ex.Message}");
            Console.WriteLine(ex.ToString());
            return (null, null, null);
        }
    }

    /// <summary>
    /// Transition the broadcast to "live" status (starts the stream).
    /// </summary>
    public async Task<bool> TransitionBroadcastToLiveAsync(string broadcastId, CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            Console.WriteLine("Error: Not authenticated.");
            return false;
        }

        try
        {
            Console.WriteLine($"Transitioning broadcast {broadcastId} to live...");

            var transitionRequest = _youtubeService.LiveBroadcasts.Transition(
                LiveBroadcastsResource.TransitionRequest.BroadcastStatusEnum.Live,
                broadcastId,
                "id,status"
            );

            var result = await transitionRequest.ExecuteAsync(cancellationToken);
            Console.WriteLine($"Broadcast is now live! Status: {result.Status.LifeCycleStatus}");
            return true;
        }
        catch (Google.GoogleApiException gae)
        {
            Console.WriteLine($"Failed to transition broadcast to live: {gae.Message}");
            Console.WriteLine($"HTTP Status: {gae.HttpStatusCode}");
            if (gae.Error != null)
            {
                Console.WriteLine($"Google API error message: {gae.Error.Message}");
                if (gae.Error.Errors != null)
                {
                    Console.WriteLine("Details:");
                    foreach (var e in gae.Error.Errors)
                    {
                        Console.WriteLine($" - {e.Domain}/{e.Reason}: {e.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine(gae.ToString());
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to transition broadcast to live: {ex.Message}");
            Console.WriteLine(ex.ToString());
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
            Console.WriteLine("YouTube service not initialized.");
            return false;
        }

        if (string.IsNullOrEmpty(streamId)) streamId = _lastCreatedStreamId;
        if (string.IsNullOrEmpty(streamId))
        {
            Console.WriteLine("No streamId available to poll ingestion status.");
            return false;
        }

        timeout ??= TimeSpan.FromSeconds(30);
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
                    Console.WriteLine($"Polled stream status: streamId={streamId} status={status}");
                    // Log the full LiveStream object for diagnosis
                    try
                    {
                        Console.WriteLine("LiveStream poll response:");
                        Console.WriteLine(JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch { }
                    if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Ingestion is active.");
                        return true;
                    }
                }
                await Task.Delay(2000, cancellationToken);
            }
            Console.WriteLine("Ingestion did not become active within timeout.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while polling ingestion status: {ex.Message}");
            Console.WriteLine(ex.ToString());
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
            Console.WriteLine("Error: Not authenticated.");
            return false;
        }

        try
        {
            Console.WriteLine($"Ending broadcast {broadcastId}...");

            var transitionRequest = _youtubeService.LiveBroadcasts.Transition(
                LiveBroadcastsResource.TransitionRequest.BroadcastStatusEnum.Complete,
                broadcastId,
                "id,status"
            );

            var result = await transitionRequest.ExecuteAsync(cancellationToken);
            Console.WriteLine($"Broadcast ended. Status: {result.Status.LifeCycleStatus}");
            return true;
        }
        catch (Google.GoogleApiException gae)
        {
            Console.WriteLine($"Failed to end broadcast: {gae.Message}");
            Console.WriteLine($"HTTP Status: {gae.HttpStatusCode}");
            if (gae.Error != null)
            {
                Console.WriteLine($"Google API error message: {gae.Error.Message}");
                if (gae.Error.Errors != null)
                {
                    Console.WriteLine("Details:");
                    foreach (var e in gae.Error.Errors)
                    {
                        Console.WriteLine($" - {e.Domain}/{e.Reason}: {e.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine(gae.ToString());
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to end broadcast: {ex.Message}");
            Console.WriteLine(ex.ToString());
            return false;
        }
    }

    /// <summary>
    /// Wait for ingestion to be active and attempt to transition the broadcast to live with retries and diagnostics.
    /// </summary>
    public async Task<bool> TransitionBroadcastToLiveWhenReadyAsync(string broadcastId, TimeSpan? maxWait = null, int maxAttempts = 12, CancellationToken cancellationToken = default)
    {
        if (_youtubeService == null)
        {
            Console.WriteLine("Error: Not authenticated.");
            return false;
        }

        // Give YouTube more time to detect ingestion for variable sources/networks
        maxWait ??= TimeSpan.FromSeconds(180);
        var attempt = 0;
        var deadline = DateTime.UtcNow + maxWait.Value;

        // First wait for ingestion to become active (polling)
        Console.WriteLine($"Waiting up to {maxWait.Value.TotalSeconds}s for ingestion to become active...");
        var ingestionOk = await WaitForIngestionAsync(null, maxWait, cancellationToken);

        if (!ingestionOk)
        {
            Console.WriteLine("Ingestion did not report active status within timeout. Will attempt transition anyway but will retry on transient errors.");
        }

        while (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                Console.WriteLine($"Attempt {attempt} to transition broadcast {broadcastId} to live...");
                // Check current broadcast lifecycle status first to avoid redundant transitions
                var listReq = _youtubeService.LiveBroadcasts.List("id,status");
                listReq.Id = broadcastId;
                var current = await listReq.ExecuteAsync(cancellationToken);
                if (current.Items != null && current.Items.Count > 0)
                {
                    var life = current.Items[0].Status?.LifeCycleStatus;
                    Console.WriteLine($"Current broadcast lifecycle: {life}");
                    if (string.Equals(life, "live", StringComparison.OrdinalIgnoreCase) || string.Equals(life, "liveStarting", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Broadcast already live/liveStarting; skipping transition.");
                        return true;
                    }
                }

                var transitionRequest = _youtubeService.LiveBroadcasts.Transition(
                    LiveBroadcastsResource.TransitionRequest.BroadcastStatusEnum.Live,
                    broadcastId,
                    "id,status"
                );

                var result = await transitionRequest.ExecuteAsync(cancellationToken);
                Console.WriteLine($"Transition succeeded: {result.Status.LifeCycleStatus}");
                return true;
            }
                catch (Google.GoogleApiException gae)
                {
                    Console.WriteLine($"Transition attempt {attempt} failed: {gae.Message}");
                    Console.WriteLine($"HTTP Status: {gae.HttpStatusCode}");

                    // Log API error details if present
                    if (gae.Error != null)
                    {
                        Console.WriteLine($"Google API error message: {gae.Error.Message}");
                        if (gae.Error.Errors != null)
                        {
                            Console.WriteLine("Details:");
                            foreach (var e in gae.Error.Errors)
                            {
                                Console.WriteLine($" - {e.Domain}/{e.Reason}: {e.Message}");
                            }

                            // If this error is a redundant or invalid transition, consider it success
                            foreach (var e in gae.Error.Errors)
                            {
                                if (string.Equals(e.Reason, "redundantTransition", StringComparison.OrdinalIgnoreCase) || string.Equals(e.Reason, "invalidTransition", StringComparison.OrdinalIgnoreCase))
                                {
                                    Console.WriteLine($"Received {e.Reason}; treating transition as success.");
                                    return true;
                                }
                            }
                        }
                    }

                    // Dump diagnostics: LiveBroadcast and LiveStream
                    try { await LogBroadcastAndStreamResourcesAsync(broadcastId, _lastCreatedStreamId, cancellationToken); } catch { }

                    if (attempt < maxAttempts)
                {
                    var backoff = TimeSpan.FromSeconds(2 * attempt);
                    Console.WriteLine($"Retrying in {backoff.TotalSeconds}s...");
                    await Task.Delay(backoff, cancellationToken);
                }
                else
                {
                    Console.WriteLine("Max transition attempts reached; giving up on transitioning the broadcast.");
                    Console.WriteLine("Note: the ffmpeg media push will continue, and viewers may still see the stream depending on YouTube settings. You can manually 'Go Live' or transition the broadcast in YouTube Studio if needed.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error trying to transition: {ex}");
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
            Console.WriteLine("YouTube service not initialized.");
            return;
        }

        try
        {
            if (!string.IsNullOrEmpty(broadcastId))
            {
                var bReq = _youtubeService.LiveBroadcasts.List("id,snippet,status,contentDetails");
                bReq.Id = broadcastId;
                var bResp = await bReq.ExecuteAsync(cancellationToken);
                Console.WriteLine("LiveBroadcast resource:");
                Console.WriteLine(JsonSerializer.Serialize(bResp, new JsonSerializerOptions { WriteIndented = true }));
            }

            if (string.IsNullOrEmpty(streamId)) streamId = _lastCreatedStreamId;
            if (!string.IsNullOrEmpty(streamId))
            {
                var sReq = _youtubeService.LiveStreams.List("id,snippet,cdn,contentDetails,status");
                sReq.Id = streamId;
                var sResp = await sReq.ExecuteAsync(cancellationToken);
                Console.WriteLine("LiveStream resource:");
                Console.WriteLine(JsonSerializer.Serialize(sResp, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error logging broadcast/stream resources: {ex.Message}");
            Console.WriteLine(ex.ToString());
        }
    }
}
