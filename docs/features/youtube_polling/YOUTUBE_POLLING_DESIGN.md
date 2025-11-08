# YouTube API Polling Strategy Design

## Problem Statement
Current implementation polls YouTube Data API aggressively (2-second intervals), exhausting daily quota and triggering 429 rate limits.

## Current Behavior Analysis
- `WaitForIngestionAsync`: Polls `LiveStreams.List` every **2 seconds** for up to 30-180s (15-90 API calls per transition)
- `TransitionBroadcastToLiveWhenReadyAsync`: Polls `LiveBroadcasts.List` before each retry (up to 12 attempts = 12+ API calls)
- Multiple concurrent operations can overlap (broadcast creation + ingestion wait + status checks)
- No idle detection or rate limiting across operations
- **Estimated cost**: 100-200 quota units per broadcast start (each List call = 1-3 units)

## Design Goals
1. **Reduce API calls by 80%+** while maintaining reliability
2. **Prevent quota exhaustion** under normal use (10-20 broadcasts/day)
3. **Maintain user experience** – acceptable wait times for state transitions
4. **Graceful degradation** – handle rate limits without breaking functionality

## Proposed Solution: Centralized Polling Manager

### Architecture
```
YouTubePollingManager (singleton)
  ├─ RateLimiter (token bucket: 100 requests/minute)
  ├─ BackoffScheduler (exponential + jitter)
  ├─ RequestCache (deduplicate identical requests within 5s window)
  └─ HealthMonitor (detect idle periods, adjust intervals)
```

### Configuration Schema
```json
{
  "YouTube": {
    "Polling": {
      "Enabled": true,
      "BaseIntervalSeconds": 15,          // Default poll interval (was 2s)
      "MinIntervalSeconds": 10,           // Minimum when urgent (was 2s)
      "MaxIntervalSeconds": 60,           // Maximum during idle
      "IdleThresholdMinutes": 5,          // Time before entering idle mode
      "BackoffMultiplier": 1.5,           // Exponential backoff factor
      "MaxJitterSeconds": 5,              // Random jitter to prevent thundering herd
      "RequestsPerMinute": 100,           // Rate limit (YouTube allows ~1000/min but we stay conservative)
      "CacheDurationSeconds": 5           // Deduplicate identical requests
    }
  }
}
```

### Implementation Changes

#### 1. YouTubePollingManager Service
```csharp
public class YouTubePollingManager
{
    private readonly RateLimiter _rateLimiter;
    private readonly ConcurrentDictionary<string, CachedResponse> _cache;
    private DateTime _lastApiCall = DateTime.UtcNow;
    
    // Wraps all YouTube API calls with rate limiting + caching
    public async Task<T> ExecuteWithRateLimitAsync<T>(
        Func<Task<T>> apiCall, 
        string cacheKey,
        CancellationToken ct);
    
    // Smart polling: adjusts interval based on context
    public async Task<T> PollUntilConditionAsync<T>(
        Func<Task<T>> fetchFunc,
        Func<T, bool> condition,
        TimeSpan timeout,
        PollContext context,
        CancellationToken ct);
}
```

#### 2. Updated WaitForIngestionAsync
**Before**: Poll every 2s
**After**: Poll every 15s initially, reduce to 10s after first check, add jitter
```csharp
// OLD: await Task.Delay(2000, ct);
// NEW:
var interval = _pollingManager.CalculateInterval(context: "ingestion", attempt: attemptCount);
await Task.Delay(interval, ct);
```

#### 3. Updated TransitionBroadcastToLiveWhenReadyAsync
**Before**: Check status before every retry (12 calls max)
**After**: Use cached status if available (<5s old), reduce retries to 5
```csharp
// Check with cache
var status = await _pollingManager.GetBroadcastStatusAsync(broadcastId, ct);
```

#### 4. Idle Detection
- Track time since last stream/broadcast activity
- After 5 minutes idle, increase polling intervals to 60s
- When new activity detected, reset to base interval (15s)

#### 5. Health Heuristics
- **Local stream health**: If ffmpeg RTMP output is stable for 30s, assume ingestion likely healthy
- **Reduce verification polls**: Only verify YouTube ingestion on:
  - Initial broadcast start
  - After detected disconnect/error
  - User-triggered "force go live" action
- **Skip redundant calls**: Don't poll if we already transitioned to "live" status

### Acceptance Criteria
✅ Default configuration reduces API calls to **<30 per broadcast** (vs 100-200 current)
✅ Manual tests show broadcasts still go live within 60-90 seconds
✅ Rate limiter prevents 429 errors under normal load (10 broadcasts/day)
✅ Configuration options allow tuning for reliability vs quota tradeoff
✅ Logs include: API call counts, current interval, cache hits, rate limit delays

### Migration Plan
1. **Phase 1**: Add YouTubePollingManager, wrap existing calls (no behavior change)
2. **Phase 2**: Increase base intervals to 15s, enable caching
3. **Phase 3**: Add idle detection and health heuristics
4. **Phase 4**: Monitor for 48h, tune intervals based on success rate

### Rollback Plan
- Set `YouTube:Polling:Enabled=false` to revert to direct calls
- Set `YouTube:Polling:BaseIntervalSeconds=2` to restore aggressive polling
- Config changes take effect immediately (no restart required if using IOptionsMonitor)
