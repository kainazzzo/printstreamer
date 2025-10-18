using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal class YouTubeBroadcastService : IDisposable
{
	private readonly IConfiguration _config;
	private Google.Apis.YouTube.v3.YouTubeService? _youtubeService;
	private UserCredential? _credential;
	private readonly string _tokenPath;

	public YouTubeBroadcastService(IConfiguration config)
	{
		_config = config;
		_tokenPath = Path.Combine(Directory.GetCurrentDirectory(), "youtube_tokens");
	}

	/// <summary>
	/// Authenticate with YouTube using OAuth2. Opens a browser on first run to get user consent.
	/// Stores refresh token locally for subsequent runs.
	/// </summary>
	public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			var clientId = _config["YouTube:OAuth:ClientId"];
			var clientSecret = _config["YouTube:OAuth:ClientSecret"];

			if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
			{
				Console.WriteLine("Error: YouTube OAuth ClientId and ClientSecret are required in configuration.");
				return false;
			}

			Console.WriteLine("Authenticating with YouTube...");

			var secrets = new ClientSecrets
			{
				ClientId = clientId,
				ClientSecret = clientSecret
			};

			// Request YouTube data API scopes
			var scopes = new[] { Google.Apis.YouTube.v3.YouTubeService.Scope.Youtube };

			// Try the normal automatic browser flow first
			try
			{
				_credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
					secrets,
					scopes,
					"user",
					cancellationToken,
					new FileDataStore(_tokenPath, fullPath: true)
				);
			}
			catch (Exception exAuto)
			{
				Console.WriteLine($"Automatic browser launch failed: {exAuto.Message}");
				Console.WriteLine("Falling back to manual authorization. Please open the URL below in a browser and paste the code here.");
				// Manual flow: create the authorization URL and prompt user for code
				var flow = new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow(
					new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow.Initializer
					{
						ClientSecrets = secrets,
						Scopes = scopes,
						DataStore = new FileDataStore(_tokenPath, fullPath: true)
					}
				);

				// Use out-of-band redirect (copy-paste) so user can paste the code
				var codeRequest = flow.CreateAuthorizationCodeRequest("urn:ietf:wg:oauth:2.0:oob");
				var url = codeRequest.Build().ToString();
				Console.WriteLine(url);
				Console.Write("Enter authorization code: ");
				var code = Console.ReadLine();
				if (string.IsNullOrWhiteSpace(code))
				{
					Console.WriteLine("No code entered; authentication canceled.");
					return false;
				}

				var token = await flow.ExchangeCodeForTokenAsync("user", code.Trim(), "urn:ietf:wg:oauth:2.0:oob", cancellationToken);
				_credential = new UserCredential(flow, "user", token);
			}

			Console.WriteLine("Authentication successful!");

			// Create YouTube API service
			_youtubeService = new Google.Apis.YouTube.v3.YouTubeService(new BaseClientService.Initializer
			{
				HttpClientInitializer = _credential,
				ApplicationName = "PrintStreamer"
			});

			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Authentication failed: {ex.Message}");
			Console.WriteLine(ex.ToString());
			return false;
		}
	}

	/// <summary>
	/// Create a YouTube live broadcast and stream, bind them together, and return the RTMP ingestion URL.
	/// </summary>
	public async Task<(string? rtmpUrl, string? streamKey, string? broadcastId)> CreateLiveBroadcastAsync(CancellationToken cancellationToken = default)
	{
		if (_youtubeService == null || _credential == null)
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
	public async Task<bool> TransitionBroadcastToLiveWhenReadyAsync(string broadcastId, TimeSpan? maxWait = null, int maxAttempts = 3, CancellationToken cancellationToken = default)
	{
		if (_youtubeService == null)
		{
			Console.WriteLine("Error: Not authenticated.");
			return false;
		}

		maxWait ??= TimeSpan.FromSeconds(90);
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

				// Dump diagnostics: LiveBroadcast and LiveStream
				try { await LogBroadcastAndStreamResourcesAsync(broadcastId, null, cancellationToken); } catch { }

				if (attempt < maxAttempts)
				{
					var backoff = TimeSpan.FromSeconds(2 * attempt);
					Console.WriteLine($"Retrying in {backoff.TotalSeconds}s...");
					await Task.Delay(backoff, cancellationToken);
				}
				else
				{
					Console.WriteLine("Max transition attempts reached; giving up.");
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
