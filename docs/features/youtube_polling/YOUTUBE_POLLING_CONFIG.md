# YouTube API Polling Configuration

## Overview
PrintStreamer includes an intelligent polling manager to minimize YouTube Data API quota consumption while maintaining reliable live broadcasts. By default, API calls are reduced by ~80% compared to aggressive polling.

## Configuration

Add to your `appsettings.json` or `appsettings.Local.json`:

```json
{
  "YouTube": {
    "Polling": {
      "Enabled": true,
      "BaseIntervalSeconds": 15,
      "MinIntervalSeconds": 10,
      "MaxIntervalSeconds": 60,
      "IdleThresholdMinutes": 5,
      "BackoffMultiplier": 1.5,
      "MaxJitterSeconds": 5,
      "RequestsPerMinute": 100,
      "CacheDurationSeconds": 5
    }
  }
}
```

## Settings Explained

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Master switch. Set `false` to revert to direct API calls (for troubleshooting) |
| `BaseIntervalSeconds` | `15` | Default polling interval (was 2s before optimization) |
| `MinIntervalSeconds` | `10` | Minimum interval when urgent checks are needed |
| `MaxIntervalSeconds` | `60` | Maximum interval during idle periods |
| `IdleThresholdMinutes` | `5` | Minutes of inactivity before entering idle mode |
| `BackoffMultiplier` | `1.5` | Exponential backoff factor for retries |
| `MaxJitterSeconds` | `5` | Random jitter to prevent thundering herd |
| `RequestsPerMinute` | `100` | Rate limit (YouTube allows ~1000/min, we stay conservative) |
| `CacheDurationSeconds` | `5` | Deduplicate identical requests within this window |

## Tuning Presets

### Balanced (Default)
Best for most users - good reliability with significant quota savings.
```json
{
  "BaseIntervalSeconds": 15,
  "MinIntervalSeconds": 10,
  "MaxIntervalSeconds": 60
}
```

### High Reliability
More frequent checks, faster ingestion detection. Use if broadcasts frequently fail to go live.
```json
{
  "BaseIntervalSeconds": 10,
  "MinIntervalSeconds": 5,
  "MaxIntervalSeconds": 30,
  "RequestsPerMinute": 150
}
```

### Maximum Quota Conservation
Minimal API usage. Use if you're hitting daily quota limits.
```json
{
  "BaseIntervalSeconds": 30,
  "MinIntervalSeconds": 20,
  "MaxIntervalSeconds": 120,
  "RequestsPerMinute": 50
}
```

### Troubleshooting
Revert to original aggressive polling (not recommended for production).
```json
{
  "Enabled": false
}
```

## Monitoring

View real-time statistics:
```bash
curl http://localhost:8080/api/youtube/polling/status
```

Response example:
```json
{
  "totalRequests": 42,
  "cacheHits": 18,
  "rateLimitWaits": 0,
  "idleTimeMinutes": 0.5,
  "isIdle": false,
  "cachedItemCount": 3
}
```

Clear cache (useful for testing):
```bash
curl -X POST http://localhost:8080/api/youtube/polling/clear-cache
```

## Performance Impact

**Before optimization:**
- Polling interval: 2 seconds
- API calls per broadcast: 100-200
- Quota units consumed: 100-200 per broadcast

**After optimization (default settings):**
- Polling interval: 15+ seconds (adaptive)
- API calls per broadcast: <30
- Quota units consumed: <30 per broadcast
- **Reduction: ~80-85%**

## Troubleshooting

### Broadcasts take too long to go live
**Symptom**: Ingestion detects slowly, transition delays

**Solution**: Reduce intervals
```json
{
  "BaseIntervalSeconds": 10,
  "MinIntervalSeconds": 5
}
```

### Hitting YouTube API quota limits
**Symptom**: 429 errors, "quotaExceeded" messages

**Solution 1**: Increase intervals
```json
{
  "BaseIntervalSeconds": 30,
  "MaxIntervalSeconds": 120
}
```

**Solution 2**: Lower rate limit
```json
{
  "RequestsPerMinute": 50
}
```

### Need to debug polling issues
**Symptom**: Unexpected behavior, need original polling

**Solution**: Temporarily disable manager
```json
{
  "Enabled": false
}
```

**Note**: Original polling uses 2-second intervals and will consume quota rapidly.

## How It Works

1. **Rate Limiting**: Token bucket algorithm prevents exceeding configured requests/minute
2. **Caching**: Identical API calls within 5s window return cached responses
3. **Backoff**: Failed requests retry with exponentially increasing delays plus jitter
4. **Idle Detection**: After 5 minutes without activity, polling slows to maximum interval
5. **Smart Recovery**: When activity resumes, polling returns to base interval

## Logging

The polling manager emits structured logs at `Debug` and `Information` levels:

```
[YouTubePollingManager] Cache hit for broadcast-status:abc123
[YouTubePollingManager] Starting poll: context=ingestion:xyz789, timeout=180s
[YouTubePollingManager] YouTube API call broadcast-transition:abc123:3 completed in 245ms (total: 42, cache hits: 18)
[YouTubePollingManager] Poll succeeded: context=ingestion:xyz789, attempts=4
```

Enable debug logging in `appsettings.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "PrintStreamer.Services.YouTubePollingManager": "Debug"
    }
  }
}
```

## Implementation Details

- **Manager**: `Services/YouTubePollingManager.cs`
- **Options**: `Services/YouTubePollingOptions.cs`
- **Integration**: `Services/YouTubeControlService.cs` (WaitForIngestionAsync, TransitionBroadcastToLiveWhenReadyAsync)
- **DI Registration**: `Program.cs`
- **Design Doc**: `YOUTUBE_POLLING_DESIGN.md`
