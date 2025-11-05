using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PrintStreamer.Services
{
    internal class YouTubeReuseManager
    {
        private readonly YouTubeReuseOptions _opts;
        private readonly YouTubeBroadcastStore _store;
        private readonly IConfiguration _config;
        private readonly ILogger<YouTubeReuseManager> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly YouTubePollingManager? _pollingManager;
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        public YouTubeReuseManager(IOptions<YouTubeReuseOptions> opts, YouTubeBroadcastStore store, IConfiguration config, ILogger<YouTubeReuseManager> logger, ILoggerFactory loggerFactory, YouTubePollingManager? pollingManager = null)
        {
            _opts = opts?.Value ?? new YouTubeReuseOptions();
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _pollingManager = pollingManager;
        }

        public async Task<(string? broadcastId, string? rtmpUrl, string? streamKey)> GetOrCreateBroadcastAsync(string title, string context, CancellationToken cancellationToken = default)
        {
            if (!_opts.Enabled)
            {
                _logger.LogDebug("Reuse disabled; creating new broadcast");
                return await CreateAndPersistAsync(title, context, cancellationToken);
            }

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var existing = await _store.GetAsync(context);
                if (existing != null && !IsExpired(existing))
                {
                    if (await ValidateBroadcastAsync(existing, cancellationToken))
                    {
                        _logger.LogInformation("Reusing existing broadcast: {BroadcastId} for context {Context}", existing.BroadcastId, context);
                        return (existing.BroadcastId, existing.RtmpUrl, existing.StreamKey);
                    }
                    else
                    {
                        _logger.LogInformation("Stored broadcast invalid/expired; removing record for context {Context}", context);
                        await _store.RemoveAsync(context);
                    }
                }

                return await CreateAndPersistAsync(title, context, cancellationToken);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private bool IsExpired(BroadcastRecord r)
        {
            return DateTime.UtcNow - r.CreatedAtUtc > TimeSpan.FromMinutes(r.TtlMinutes);
        }

        private async Task<bool> ValidateBroadcastAsync(BroadcastRecord r, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(r.BroadcastId)) return false;

                // Use YouTubeControlService to authenticate and perform a cheap check
                var ytLogger = _loggerFactory.CreateLogger<YouTubeControlService>();
                using var yt = new YouTubeControlService(_config, ytLogger, _pollingManager);
                if (!await yt.AuthenticateAsync(ct))
                {
                    _logger.LogWarning("YouTube authentication failed when validating broadcast {BroadcastId}", r.BroadcastId);
                    return false;
                }

                var privacy = await yt.GetBroadcastPrivacyAsync(r.BroadcastId, ct);
                if (privacy == null)
                {
                    _logger.LogInformation("Broadcast {BroadcastId} not found", r.BroadcastId);
                    return false;
                }

                if (_opts.OnlyUnlistedOrPrivateForReuse && string.Equals(privacy, "public", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Rejecting reuse for public broadcast {BroadcastId}", r.BroadcastId);
                    return false;
                }

                // Accept if broadcast exists. Stream key/RTMP may still be valid but we prefer the lightweight check.
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error validating broadcast record");
                return false;
            }
        }

        private async Task<(string? broadcastId, string? rtmpUrl, string? streamKey)> CreateAndPersistAsync(string title, string context, CancellationToken ct)
        {
            try
            {
                var ytLogger = _loggerFactory.CreateLogger<YouTubeControlService>();
                using var yt = new YouTubeControlService(_config, ytLogger, _pollingManager);
                if (!await yt.AuthenticateAsync(ct))
                {
                    _logger.LogWarning("YouTube auth failed when creating broadcast");
                    return (null, null, null);
                }

                var res = await yt.CreateLiveBroadcastAsync(ct);
                if (res.broadcastId == null || res.rtmpUrl == null || res.streamKey == null)
                {
                    _logger.LogWarning("Failed to create broadcast via YouTubeControlService");
                    return (res.broadcastId, res.rtmpUrl, res.streamKey);
                }

                var rec = new BroadcastRecord
                {
                    BroadcastId = res.broadcastId,
                    RtmpUrl = res.rtmpUrl,
                    StreamKey = res.streamKey,
                    Context = context,
                    CreatedAtUtc = DateTime.UtcNow,
                    TtlMinutes = _opts.TtlMinutes
                };

                await _store.SaveAsync(rec);
                _logger.LogInformation("Created and persisted broadcast {BroadcastId} for context {Context}", rec.BroadcastId, context);
                return (rec.BroadcastId, rec.RtmpUrl, rec.StreamKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating broadcast");
                return (null, null, null);
            }
        }
    }
}
