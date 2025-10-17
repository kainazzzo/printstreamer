using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
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

			// Use FileDataStore to save/load refresh token
			_credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
				secrets,
				scopes,
				"user",
				cancellationToken,
				new FileDataStore(_tokenPath, fullPath: true)
			);

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

			Console.WriteLine($"RTMP URL: {rtmpUrl}");
			Console.WriteLine($"Stream Key: {streamKey}");
			Console.WriteLine($"Broadcast URL: https://www.youtube.com/watch?v={createdBroadcast.Id}");

			return (rtmpUrl, streamKey, createdBroadcast.Id);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Failed to create live broadcast: {ex.Message}");
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
		catch (Exception ex)
		{
			Console.WriteLine($"Failed to transition broadcast to live: {ex.Message}");
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
		catch (Exception ex)
		{
			Console.WriteLine($"Failed to end broadcast: {ex.Message}");
			return false;
		}
	}

	public void Dispose()
	{
		_youtubeService?.Dispose();
	}
}
