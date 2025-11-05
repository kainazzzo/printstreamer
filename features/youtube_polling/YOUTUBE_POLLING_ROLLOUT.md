# YouTube Polling Optimization - Rollout Plan

## Objective
Deploy YouTube API polling optimization that reduces quota consumption by ~80% while maintaining broadcast reliability.

## Pre-Deployment Checklist

### Code Review
- [x] YouTubePollingManager implements rate limiting, caching, backoff, and idle detection
- [x] Configuration added to appsettings.json with sensible defaults
- [x] YouTubeControlService integrated with polling manager
- [x] StreamOrchestrator passes polling manager to YouTube service instances
- [x] API endpoints updated to inject polling manager
- [x] Monitoring endpoints added (`/api/youtube/polling/status`, `/api/youtube/polling/clear-cache`)
- [x] Build succeeds with no errors or warnings

### Documentation
- [x] Design document created (`YOUTUBE_POLLING_DESIGN.md`)
- [x] Configuration guide created (`YOUTUBE_POLLING_CONFIG.md`)
- [x] PLANNED_FEATURES.md updated with implementation notes
- [x] Rollback procedure documented

## Deployment Strategy

### Phase 1: Staging/Testing (24-48 hours)
**Goal**: Verify polling reduction without impacting broadcast reliability

#### Steps
1. Deploy to staging/test environment
2. Update configuration to enable extended logging:
   ```json
   {
     "Logging": {
       "LogLevel": {
         "PrintStreamer.Services.YouTubePollingManager": "Debug",
         "YouTubeControlService": "Debug"
       }
     },
     "YouTube": {
       "Polling": {
         "Enabled": true,
         "BaseIntervalSeconds": 15,
         "MinIntervalSeconds": 10,
         "MaxIntervalSeconds": 60
       }
     }
   }
   ```

3. **Test Cases**:
   - [ ] Start local stream (no YouTube) - verify no polling occurs
   - [ ] Start YouTube broadcast - verify polling at ~15s intervals
   - [ ] Monitor `/api/youtube/polling/status` - verify metrics increment
   - [ ] Let broadcast run for 10 minutes - verify ingestion detection
   - [ ] Let system idle for 6+ minutes - verify idle mode activates (60s intervals)
   - [ ] Start another broadcast after idle - verify return to 15s intervals
   - [ ] Simulate API errors (disconnect network briefly) - verify backoff behavior
   - [ ] End broadcast - verify cleanup

4. **Success Criteria**:
   - [ ] Broadcasts successfully transition to "live" within 90 seconds
   - [ ] Total API calls <30 per broadcast (vs 100-200 before)
   - [ ] No 429 rate limit errors
   - [ ] Logs show cache hits > 30%
   - [ ] Logs show rate limit waits = 0 (under normal load)

5. **Failure Scenarios**:
   - If broadcasts fail to go live: reduce `BaseIntervalSeconds` to 10
   - If hitting rate limits: reduce `RequestsPerMinute` to 50
   - If ingestion detection too slow: reduce `MinIntervalSeconds` to 5

### Phase 2: Limited Production (1 week)
**Goal**: Validate under real-world usage patterns

#### Steps
1. Deploy to production with default settings
2. Monitor for 7 days:
   - Daily API quota consumption (YouTube Developer Console)
   - Broadcast success rate (manual tracking)
   - Error logs (search for "429", "quotaExceeded", "TransitionBroadcastToLiveWhenReadyAsync failed")
   - Polling stats via API endpoint

3. **Metrics to Track**:
   - API quota used per day (before: ~2000-5000 for 10 broadcasts, after: <500)
   - Broadcast start success rate (target: >95%)
   - Average time to "live" status (target: <90 seconds)
   - Rate limit events (target: 0)

4. **Monitoring Commands**:
   ```bash
   # Check polling stats
   curl http://localhost:8080/api/youtube/polling/status | jq
   
   # Watch logs in real-time
   docker logs -f printstreamer | grep YouTubePolling
   
   # Count API calls from logs
   docker logs printstreamer | grep "YouTube API call" | wc -l
   ```

5. **Adjustment Criteria**:
   - If quota still too high: increase `BaseIntervalSeconds` to 20-30
   - If broadcasts frequently fail: decrease `BaseIntervalSeconds` to 10
   - If users report slow "go live": decrease `MinIntervalSeconds` to 5

### Phase 3: Full Production (Ongoing)
**Goal**: Stable operation with optimized quota usage

#### Steps
1. Remove debug logging, keep Info level:
   ```json
   {
     "Logging": {
       "LogLevel": {
         "PrintStreamer.Services.YouTubePollingManager": "Information"
       }
     }
   }
   ```

2. Set up periodic monitoring (weekly):
   - Review YouTube API quota usage trends
   - Check for anomalies in broadcast success rate
   - Monitor polling stats endpoint

3. Document final tuned settings in production config

## Rollback Procedures

### Immediate Rollback (No Code Changes)
If critical issues occur, disable polling manager via configuration:

**Option 1: Environment variable**
```bash
export YouTube__Polling__Enabled=false
docker restart printstreamer
```

**Option 2: Update appsettings.json**
```json
{
  "YouTube": {
    "Polling": {
      "Enabled": false
    }
  }
}
```

Restart service. Polling reverts to original 2-second intervals.

**Impact**: Quota consumption returns to pre-optimization levels (100-200 calls per broadcast)

### Partial Rollback (Tune Settings)
If broadcasts are unreliable but you want to keep some optimization:

```json
{
  "YouTube": {
    "Polling": {
      "Enabled": true,
      "BaseIntervalSeconds": 5,     // More aggressive
      "MinIntervalSeconds": 2,       // Match original
      "MaxIntervalSeconds": 30,
      "RequestsPerMinute": 200       // Higher limit
    }
  }
}
```

**Impact**: Moderate quota savings (~50%) with higher reliability

### Full Code Rollback
If catastrophic failure requires reverting code changes:

```bash
git revert <commit-hash>
git push origin main
# Redeploy
```

**Files to revert**:
- `Services/YouTubePollingManager.cs`
- `Services/YouTubePollingOptions.cs`
- `Services/YouTubeControlService.cs` (constructor and methods)
- `Services/StreamOrchestrator.cs` (constructor injection)
- `Program.cs` (DI registration, API endpoints)
- `appsettings.json` (polling config)

## Success Metrics

### Baseline (Before Optimization)
- API calls per broadcast: 100-200
- Polling interval: 2 seconds
- Quota units per broadcast: 100-200
- Daily quota usage (10 broadcasts): ~2000 units

### Target (After Optimization)
- API calls per broadcast: <30
- Polling interval: 15+ seconds (adaptive)
- Quota units per broadcast: <30
- Daily quota usage (10 broadcasts): <500 units
- **Reduction: 75-80%**

### KPIs
- ✅ Broadcast success rate: >95%
- ✅ Time to "live" status: <90 seconds
- ✅ API quota reduction: >75%
- ✅ Rate limit errors: 0
- ✅ Cache hit rate: >30%

## Communication Plan

### Internal Team
- [ ] Notify team of deployment schedule
- [ ] Share monitoring dashboard/endpoints
- [ ] Document troubleshooting steps
- [ ] Schedule post-deployment review meeting

### Users (if applicable)
- [ ] Announce optimization in release notes
- [ ] Highlight quota savings
- [ ] Provide configuration guide link
- [ ] Note: no user-facing changes expected

## Post-Deployment Review

### After 1 Week
- [ ] Compare API quota usage (before vs after)
- [ ] Verify broadcast success rate maintained
- [ ] Review logs for unexpected errors
- [ ] Tune configuration if needed
- [ ] Document lessons learned

### After 1 Month
- [ ] Confirm sustained quota savings
- [ ] Assess if further optimization possible
- [ ] Update documentation with production learnings
- [ ] Mark feature as "stable" in PLANNED_FEATURES.md

## Contingency Plans

### Scenario: 429 Rate Limit Errors
**Symptoms**: "quotaExceeded" or "rateLimitExceeded" in logs

**Actions**:
1. Check `/api/youtube/polling/status` - verify `rateLimitWaits` counter
2. Reduce `RequestsPerMinute` to 50
3. Increase `BaseIntervalSeconds` to 30
4. Monitor for 1 hour
5. If persists, disable polling manager temporarily

### Scenario: Broadcasts Fail to Go Live
**Symptoms**: Streams stuck in "preview/testing", ingestion timeout errors

**Actions**:
1. Check logs for "Ingestion did not become active within timeout"
2. Reduce `BaseIntervalSeconds` to 10
3. Reduce `MinIntervalSeconds` to 5
4. Increase `RequestsPerMinute` to 150
5. Clear cache: `POST /api/youtube/polling/clear-cache`
6. Retry broadcast
7. If still failing, set `Enabled: false` (rollback)

### Scenario: Excessive Cache Misses
**Symptoms**: `cacheHits` very low (<10%), `totalRequests` unexpectedly high

**Actions**:
1. Check for rapid repeated calls with different parameters
2. Increase `CacheDurationSeconds` to 10-15
3. Review code for proper cache key generation
4. May indicate legitimate high-frequency need - adjust expectations

## Conclusion

This rollout plan provides a structured, low-risk approach to deploying the YouTube API polling optimization. The phased rollout, comprehensive monitoring, and multiple rollback options ensure that any issues can be quickly identified and resolved without impacting production broadcasts.

**Key Success Factors**:
- Extended testing period (24-48h) before production
- Real-time monitoring with clear metrics
- Multiple rollback options (config-only, partial, full)
- Clear success criteria and KPIs
- Documented troubleshooting procedures

---

**Last Updated**: 2025-01-04  
**Owner**: Development Team  
**Status**: Ready for Phase 1 (Staging)
