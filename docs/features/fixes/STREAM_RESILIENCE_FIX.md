# Stream Resilience Fix

## Problem
The YouTube live stream was ending prematurely due to:
1. FFmpeg crashes from temporary network issues or audio codec errors
2. No automatic recovery when ffmpeg process died
3. Manual restart created new broadcasts instead of reusing the existing one
4. Audio stream decoding errors would crash the entire stream

## Root Causes from Logs
- **Audio Codec Failures**: Recurring "Header missing" errors from MP3 float decoder
- **Network Issues**: Moonraker connection drops with "response ended prematurely" errors
- **Timeout**: FFmpeg process wasn't exiting gracefully within 5 seconds
- **No Resilience**: Stream crashes immediately ended the broadcast

## Solution Implemented

### 1. Auto-Restart Stream Health Monitor
**File**: `Services/StreamOrchestrator.cs`

- Added background health check timer running every 10 seconds
- Monitors if ffmpeg process is still running when broadcasting
- After 3 consecutive failures (30 seconds of downtime), automatically restarts ffmpeg
- **Critically**: Reuses existing broadcast ID and RTMP URL - no new broadcast created

**Behavior**:
```
Broadcasting...
Stream crashes
→ Health check detects crash
→ Waits 3 checks (30 seconds)
→ Auto-restarts ffmpeg with same broadcast ID
→ Stream continues on YouTube
```

### 2. Improved Audio Resilience
**File**: `Streamers/FfmpegStreamer.cs`

- Increased audio reconnect delay from 2s to 5s for more robust recovery
- Added `-read_timeout 10000000` to prevent hangs on audio stream
- FFmpeg already has `-err_detect ignore_err` and `-fflags +genpts+discardcorrupt`
- Benign MJPEG decode warnings are now suppressed to reduce log noise

### 3. Graceful FFmpeg Shutdown
**File**: `Streamers/FfmpegStreamer.cs`

- Increased shutdown timeout from 5 seconds to 15 seconds
- Allows ffmpeg more time to flush buffers and close connections cleanly
- Prevents orphaned ffmpeg processes

### 4. Error Logging Improvements
**File**: `Streamers/FfmpegStreamer.cs`

- Suppresses repeated benign MJPEG warnings (logged once per 30 seconds)
- Keeps important errors visible
- Helps distinguish real problems from noise

## Testing Recommendations

1. **Monitor during next stream**:
   - Check `/app/logs` for `[Orchestrator] Health check` messages
   - Look for auto-restart confirmations

2. **Simulate failures** (if needed):
   - Stop Moonraker service - stream should auto-restart
   - Interrupt audio stream - stream should continue with degraded audio
   - Kill ffmpeg process manually - should auto-restart within 30 seconds

3. **Verify broadcast reuse**:
   - Check YouTube Studio - should show same broadcast ID continuing
   - No new broadcasts created during auto-restarts

## Configuration

No configuration changes required. Health checks run automatically with defaults:
- Check interval: 10 seconds
- Restart after: 3 consecutive failures (30 seconds of downtime)
- FFmpeg shutdown timeout: 15 seconds

## Benefits

✓ **Continuous Streaming**: Survives temporary network glitches
✓ **Single Broadcast**: Auto-restarts reuse existing broadcast, no fragmentation
✓ **Better Recovery**: Stream resumes automatically without user intervention
✓ **Less Verbose**: Benign errors don't spam logs

## Future Improvements

1. **Audio Fallback**: If audio stream fails persistently, automatically use silent audio
2. **Smart Health Metrics**: Track uptime/downtime and adjust thresholds
3. **User Notifications**: Alert UI when auto-restart occurs
4. **Broadcast Reuse Store**: Persist last broadcast ID across container restarts
