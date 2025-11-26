using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
// Google.Apis.Util.Store is used for IDataStore implementations
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using System.Text.Json;
using Google.Apis.Util.Store;
// using Google.Apis.Auth.OAuth2.Flows; // not used with the standard broker flow
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Auth.OAuth2.Requests;
using System.Diagnostics;

namespace PrintStreamer.Services
{
	internal class YouTubeBroadcastService : IDisposable
	{
	private readonly IConfiguration _config;
	private readonly ILogger<YouTubeBroadcastService> _logger;
		private YouTubeService? _youtubeService;
		private UserCredential? _credential; // Used for user OAuth flow
		private readonly string _tokenPath;
		private readonly InMemoryDataStore _inMemoryStore = new InMemoryDataStore();
		private Task? _refreshTask;
		private CancellationTokenSource? _refreshCts;

		public YouTubeBroadcastService(IConfiguration config, ILogger<YouTubeBroadcastService> logger)
		{
			_config = config;
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
					_logger.LogError("YouTube OAuth ClientId and ClientSecret are required for user OAuth.");
					return false;
				}

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
							Scopes = scopes,
							DataStore = dataStore
						});
					_credential = new UserCredential(flow, "user", existingToken);
				}
				else
				{
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
							Console.Write("Enter authorization code: ");
							var code = Console.ReadLine();
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
						_logger.LogError(exAuth, "Authentication flow failed: {Message}", exAuth.Message);
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
									Scopes = scopes,
									DataStore = dataStore
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
					_youtubeService = new YouTubeService(new BaseClientService.Initializer
					{
						HttpClientInitializer = _credential,
						ApplicationName = "PrintStreamer"
					});
					// Start background token refresh loop
					StartTokenRefreshLoop();
					return true;
				}
				catch (TokenResponseException trex)
				{
					// If refresh was rejected due to unauthorized_client, but we have an access token available
					// in the stored TokenResponse, use it directly via GoogleCredential.FromAccessToken so we
					// don't prompt the user. Note this is a no-refresh credential and will stop working when
					// the access token expires.
					if (trex.Message != null && trex.Message.Contains("unauthorized_client") && _credential?.Token?.AccessToken != null)
					{
						_logger.LogWarning("[YouTube] Refresh rejected (unauthorized_client). Using provided access_token without refresh.");
						var accessOnly = Google.Apis.Auth.OAuth2.GoogleCredential.FromAccessToken(_credential.Token.AccessToken);
						_youtubeService = new YouTubeService(new BaseClientService.Initializer
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
								_logger.LogInformation("Refreshed access token at {Time} (len={Len})", DateTime.UtcNow.ToString("O"), token?.Length);
							}
							catch (Exception rex)
							{
								_logger.LogWarning(rex, "Failed to refresh access token: {Message}", rex.Message);
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
		public async Task<(string? rtmpUrl, string? streamKey, string? broadcastId)> CreateLiveBroadcastAsync(CancellationToken cancellationToken = default)
		{
			if (_youtubeService == null)
			{
				_logger.LogError("Not authenticated. Call AuthenticateAsync first.");
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

				_logger.LogInformation("Broadcast created with ID: {BroadcastId}", createdBroadcast.Id);

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

				_logger.LogInformation("Stream created with ID: {StreamId}", createdStream.Id);

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
				return (rtmpUrl, streamKey, createdBroadcast.Id);
			}
			catch (Google.GoogleApiException gae)
			{
				_logger.LogError(gae, "Failed to create live broadcast: {Message}", gae.Message);
				_logger.LogError("HTTP Status: {Status}", gae.HttpStatusCode);
				if (gae.Error != null)
				{
					_logger.LogError("Google API error message: {Message}", gae.Error.Message);
					if (gae.Error.Errors != null)
					{
						foreach (var e in gae.Error.Errors)
						{
							_logger.LogError(" - {Domain}/{Reason}: {Message}", e.Domain, e.Reason, e.Message);
						}
					}
				}
				else
				{
					_logger.LogError(gae, "GoogleApiException details");
				}
				return (null, null, null);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to create live broadcast: {Message}", ex.Message);
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
				_logger.LogError("Not authenticated.");
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
					_logger.LogError("Google API error message: {Message}", gae.Error.Message);
					if (gae.Error.Errors != null)
					{
						foreach (var e in gae.Error.Errors)
						{
							_logger.LogError(" - {Domain}/{Reason}: {Message}", e.Domain, e.Reason, e.Message);
						}
					}
				}
				else
				{
					_logger.LogError(gae, "GoogleApiException details");
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
				_logger.LogError("YouTube service not initialized.");
				return false;
			}

			if (string.IsNullOrEmpty(streamId)) streamId = _lastCreatedStreamId;
			if (string.IsNullOrEmpty(streamId))
			{
				_logger.LogWarning("No streamId available to poll ingestion status.");
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
						_logger.LogInformation("Polled stream status: streamId={StreamId} status={Status}", streamId, status);
						// Log the full LiveStream object for diagnosis
						try
						{
							_logger.LogDebug("LiveStream poll response: {Response}", JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
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
				_logger.LogError("Not authenticated.");
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
					_logger.LogError("Google API error message: {Message}", gae.Error.Message);
					if (gae.Error.Errors != null)
					{
						foreach (var e in gae.Error.Errors)
						{
							_logger.LogError(" - {Domain}/{Reason}: {Message}", e.Domain, e.Reason, e.Message);
						}
					}
				}
				else
				{
					_logger.LogError(gae, "GoogleApiException details");
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
		/// Wait for ingestion to be active and attempt to transition the broadcast to live with retries and diagnostics.
		/// </summary>
		public async Task<bool> TransitionBroadcastToLiveWhenReadyAsync(string broadcastId, TimeSpan? maxWait = null, int maxAttempts = 3, CancellationToken cancellationToken = default)
		{
			if (_youtubeService == null)
			{
				_logger.LogError("Not authenticated.");
				return false;
			}

			maxWait ??= TimeSpan.FromSeconds(90);
			var attempt = 0;
			var deadline = DateTime.UtcNow + maxWait.Value;

			// First wait for ingestion to become active (polling)
			_logger.LogInformation("Waiting up to {Seconds}s for ingestion to become active...", maxWait.Value.TotalSeconds);
			var ingestionOk = await WaitForIngestionAsync(null, maxWait, cancellationToken);

			if (!ingestionOk)
			{
				_logger.LogWarning("Ingestion did not report active status within timeout. Will attempt transition anyway but will retry on transient errors.");
			}

			while (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
			{
				attempt++;
				try
				{
					_logger.LogInformation("Attempt {Attempt} to transition broadcast {BroadcastId} to live...", attempt, broadcastId);
					var transitionRequest = _youtubeService.LiveBroadcasts.Transition(
						LiveBroadcastsResource.TransitionRequest.BroadcastStatusEnum.Live,
						broadcastId,
						"id,status"
					);

					var result = await transitionRequest.ExecuteAsync(cancellationToken);
					_logger.LogInformation("Transition succeeded: {Status}", result.Status.LifeCycleStatus);
					return true;
				}
				catch (Google.GoogleApiException gae)
				{
					_logger.LogError(gae, "Transition attempt {Attempt} failed: {Message}", attempt, gae.Message);
					_logger.LogError("HTTP Status: {Status}", gae.HttpStatusCode);
					if (gae.Error != null)
					{
						_logger.LogError("Google API error message: {Message}", gae.Error.Message);
						if (gae.Error.Errors != null)
						{
							foreach (var e in gae.Error.Errors)
							{
								_logger.LogError(" - {Domain}/{Reason}: {Message}", e.Domain, e.Reason, e.Message);
							}
						}
					}

					// Dump diagnostics: LiveBroadcast and LiveStream
					try { await LogBroadcastAndStreamResourcesAsync(broadcastId, null, cancellationToken); } catch { }

					if (attempt < maxAttempts)
					{
						var backoff = TimeSpan.FromSeconds(2 * attempt);
						_logger.LogInformation("Retrying in {Seconds}s...", backoff.TotalSeconds);
						await Task.Delay(backoff, cancellationToken);
					}
					else
					{
						_logger.LogWarning("Max transition attempts reached; giving up.");
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Unexpected error trying to transition: {Exception}", ex);
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
private static string RedactYouTubeResourceJson(string json)
{
    if (string.IsNullOrEmpty(json)) return json;
    try
    {
        // Redact common ingestion/stream key fields in YouTube JSON resources.
        json = System.Text.RegularExpressions.Regex.Replace(json, "(?i)\"streamName\"\\s*:\\s*\"[^\"]+\"", "\"streamName\": \"<redacted>\"");
        json = System.Text.RegularExpressions.Regex.Replace(json, "(?i)\"ingestionAddress\"\\s*:\\s*\"[^\"]+\"", "\"ingestionAddress\": \"<redacted>\"");
        // Best-effort redact nested ingestionInfo blocks
        json = System.Text.RegularExpressions.Regex.Replace(json, "(?i)\"ingestionInfo\"\\s*:\\s*\\{[^\\}]*\\}", "\"ingestionInfo\": { \"streamName\": \"<redacted>\", \"ingestionAddress\": \"<redacted>\" }");
        return json;
    }
    catch
    {
        return json;
    }
}

public async Task LogBroadcastAndStreamResourcesAsync(string? broadcastId, string? streamId, CancellationToken cancellationToken = default)
{
if (_youtubeService == null)
{
_logger.LogError("YouTube service not initialized.");
return;
}

			try
			{
				if (!string.IsNullOrEmpty(broadcastId))
				{
					var bReq = _youtubeService.LiveBroadcasts.List("id,snippet,status,contentDetails");
					bReq.Id = broadcastId;
					var bResp = await bReq.ExecuteAsync(cancellationToken);
					_logger.LogInformation("LiveBroadcast resource: {Resource}", RedactYouTubeResourceJson(JsonSerializer.Serialize(bResp, new JsonSerializerOptions { WriteIndented = true })));
				}

				if (string.IsNullOrEmpty(streamId)) streamId = _lastCreatedStreamId;
				if (!string.IsNullOrEmpty(streamId))
				{
					var sReq = _youtubeService.LiveStreams.List("id,snippet,cdn,contentDetails,status");
					sReq.Id = streamId;
					var sResp = await sReq.ExecuteAsync(cancellationToken);
					_logger.LogInformation("LiveStream resource: {Resource}", RedactYouTubeResourceJson(JsonSerializer.Serialize(sResp, new JsonSerializerOptions { WriteIndented = true })));
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error logging broadcast/stream resources: {Message}", ex.Message);
			}
		}
	}
}
