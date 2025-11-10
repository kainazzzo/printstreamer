# Implementation Plan: Data Flow Architecture Refactoring

**Version:** 1.0  
**Date:** November 7, 2025  
**Status:** Ready for Planning  
**Estimated Duration:** 2-3 hours of development

---

## Executive Summary

This document outlines the specific code changes required to implement the data flow architecture described in `DATAFLOW_ARCHITECTURE.md`.

The refactoring introduces 4 dedicated HTTP endpoints for intermediate streaming stages, creating a clean pipeline that allows independent testing, monitoring, and scaling of each component.

### Change Overview

- **New Files:** 1 (MixStreamer.cs)
- **Modified Files:** 3 (Program.cs, OverlayMjpegStreamer.cs, FfmpegStreamer.cs)
- **Breaking Changes:** None (backward compatibility maintained)
- **Database Changes:** None
- **Configuration Changes:** Optional

---

## Phase 1: Core Pipeline (Hours 0-1)

### Change 1.1: Add `/stream/source` Route

**File:** `Program.cs`

**Location:** Around line 2900 (near existing `/stream` route)

**Current Code:**
```csharp
app.MapGet("/stream", async (HttpContext ctx) => await webcamManager.HandleStreamRequest(ctx));
```

**Change:**
```csharp
// Both endpoints point to the same handler (backward compatibility + new naming)
app.MapGet("/stream", async (HttpContext ctx) => await webcamManager.HandleStreamRequest(ctx));
app.MapGet("/stream/source", async (HttpContext ctx) => await webcamManager.HandleStreamRequest(ctx));
```

**Rationale:** 
- Maintains backward compatibility with `/stream`
- Introduces new `/stream/source` naming convention
- No code logic changes needed

**Testing:**
```bash
curl http://localhost:8080/stream/source -o test.mjpeg
# Should get MJPEG stream or fallback black image
```

---

### Change 1.2: Add `/stream/audio` Route

**File:** `Program.cs`

**Location:** Around line 3050 (near existing `/api/audio/stream` route)

**Current Code:**
```csharp
app.MapGet("/api/audio/stream", async (HttpContext ctx) =>
{
    var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
    var enabled = cfg.GetValue<bool?>("Audio:Enabled") ?? true;
    if (!enabled)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("Audio stream disabled");
        return;
    }

    ctx.Response.StatusCode = 200;
    ctx.Response.Headers["Content-Type"] = "audio/mpeg";
    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
    ctx.Response.Headers["Pragma"] = "no-cache";
    await ctx.Response.Body.FlushAsync();

    var broadcaster = ctx.RequestServices.GetRequiredService<AudioBroadcastService>();
    try
    {
        await foreach (var chunk in broadcaster.Stream(ctx.RequestAborted))
        {
            await ctx.Response.Body.WriteAsync(chunk, 0, chunk.Length, ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Client stream error");
    }
});
```

**Change (Option A - Extract to Helper):**
```csharp
// Create a helper method
async Task ServeAudioStream(HttpContext ctx)
{
    var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
    var enabled = cfg.GetValue<bool?>("Audio:Enabled") ?? true;
    if (!enabled)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("Audio stream disabled");
        return;
    }

    ctx.Response.StatusCode = 200;
    ctx.Response.Headers["Content-Type"] = "audio/mpeg";
    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
    ctx.Response.Headers["Pragma"] = "no-cache";
    await ctx.Response.Body.FlushAsync();

    var broadcaster = ctx.RequestServices.GetRequiredService<AudioBroadcastService>();
    try
    {
        await foreach (var chunk in broadcaster.Stream(ctx.RequestAborted))
        {
            await ctx.Response.Body.WriteAsync(chunk, 0, chunk.Length, ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Client stream error");
    }
}

// Map both routes to the helper
app.MapGet("/api/audio/stream", ServeAudioStream);
app.MapGet("/stream/audio", ServeAudioStream);
```

**Change (Option B - Simpler Duplication):**
```csharp
// Just add the new route
app.MapGet("/stream/audio", async (HttpContext ctx) =>
{
    var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
    var enabled = cfg.GetValue<bool?>("Audio:Enabled") ?? true;
    if (!enabled)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("Audio stream disabled");
        return;
    }

    ctx.Response.StatusCode = 200;
    ctx.Response.Headers["Content-Type"] = "audio/mpeg";
    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
    ctx.Response.Headers["Pragma"] = "no-cache";
    await ctx.Response.Body.FlushAsync();

    var broadcaster = ctx.RequestServices.GetRequiredService<AudioBroadcastService>();
    try
    {
        await foreach (var chunk in broadcaster.Stream(ctx.RequestAborted))
        {
            await ctx.Response.Body.WriteAsync(chunk, 0, chunk.Length, ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Client stream error");
    }
});
```

**Recommendation:** Option B (simpler, less refactoring risk)

**Testing:**
```bash
curl http://localhost:8080/stream/audio -o test.mp3
# Should get MP3 audio stream
```

---

### Change 1.3: Update OverlayMjpegStreamer Source URL

**File:** `Streamers/OverlayMjpegStreamer.cs`

**Location:** Around line 39-45

**Current Code:**
```csharp
var source = _config.GetValue<string>("Stream:Source");
if (string.IsNullOrWhiteSpace(source))
{
    _logger.LogError("[{ContextLabel}] Stream source not configured", contextLabel);
    // ...
}
```

**Change:**
```csharp
// Use local /stream/source endpoint instead of raw camera
var source = _config.GetValue<string>("Overlay:StreamSource") ?? 
             "http://127.0.0.1:8080/stream/source";
             
if (string.IsNullOrWhiteSpace(source))
{
    _logger.LogError("[{ContextLabel}] Stream source not configured", contextLabel);
    _logger.LogError("[{ContextLabel}] Using default: {DefaultSource}", contextLabel, source);
    source = "http://127.0.0.1:8080/stream/source";
}

_logger.LogInformation("[{ContextLabel}] Using video source: {Source}", contextLabel, source);
```

**Alternative (Backward Compatible):**
```csharp
/*
Only override if Stream:Source points to an external camera.
When the source is external, route it through the local /stream/source
endpoint so downstream overlay and buffering stages can consume it.
*/
var source = _config.GetValue<string>("Stream:Source");

// If it's an external camera, route it through the local /stream/source endpoint for overlay processing
if (!string.IsNullOrWhiteSpace(source) && 
    !source.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
    !source.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
{
    // External camera source - route through local /stream/source
    source = "http://127.0.0.1:8080/stream/source";
    _logger.LogInformation("[{ContextLabel}] Routing camera through local /stream/source", contextLabel);
}
```

**Recommendation:** Use the alternative (maintains backward compatibility)

**Testing:**
```bash
curl http://localhost:8080/stream/overlay -o test_overlay.mjpeg
# Should get MJPEG with overlays (or timeout if ffmpeg fails)
```

---

### Change 1.4: Build and Verify Phase 1

**Steps:**
```bash
cd /home/ddatti/printstreamer
dotnet build
dotnet run --configuration Debug
```

**Verification Checklist:**
- ✓ Build succeeds with no errors
- ✓ Application starts
- ✓ `/stream/source` returns MJPEG or black fallback
- ✓ `/stream/audio` returns MP3 audio
- ✓ `/stream/overlay` returns overlayed MJPEG
- ✓ Logs show no warnings about missing sources

---

## Phase 2: Mix Pipeline (Hours 1-2)

### Change 2.1: Create MixStreamer Class

**File:** `Streamers/MixStreamer.cs` (NEW FILE)

**Full Content:**
```csharp
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintStreamer.Interfaces;

namespace PrintStreamer.Streamers
{
    /// <summary>
    /// Combines video and audio streams into a single H.264 + AAC stream.
    /// Reads from /stream/overlay (video) and /stream/audio (audio).
    /// </summary>
    internal sealed class MixStreamer : IStreamer
    {
        private readonly IConfiguration _config;
        private readonly HttpContext _ctx;
        private readonly ILogger<MixStreamer> _logger;
        private Process? _proc;
        private TaskCompletionSource<object?> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExitTask => _exitTcs.Task;

        public MixStreamer(IConfiguration config, HttpContext ctx, ILogger<MixStreamer> logger)
        {
            _config = config;
            _ctx = ctx;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            const string contextLabel = "Video+Audio Mix";

            try
            {
                var overlaySource = "http://127.0.0.1:8080/stream/overlay";
                var audioSource = "http://127.0.0.1:8080/stream/audio";
                var audioEnabled = _config.GetValue<bool?>("Audio:Enabled") ?? true;

                _logger.LogInformation("[{ContextLabel}] Starting mix: video={Video} audio={Audio}", 
                    contextLabel, overlaySource, audioEnabled ? audioSource : "disabled");

                // Set HTTP response headers
                if (!_ctx.Response.HasStarted)
                {
                    _ctx.Response.StatusCode = 200;
                    _ctx.Response.ContentType = "video/mp4";
                    _ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                    _ctx.Response.Headers["Pragma"] = "no-cache";
                }

                // Build ffmpeg command
                var args = BuildFfmpegArgs(overlaySource, audioSource, audioEnabled);

                _logger.LogInformation("[{ContextLabel}] ffmpeg args: {Args}", contextLabel, args);

                _proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                using var reg = cancellationToken.Register(() => { try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { } });

                _proc.Start();

                // Stream ffmpeg output to HTTP response
                await _proc.StandardOutput.BaseStream.CopyToAsync(_ctx.Response.Body, 64 * 1024, cancellationToken);
                
                try { await _ctx.Response.Body.FlushAsync(cancellationToken); } catch { }

                _exitTcs.TrySetResult(null);
                _logger.LogInformation("[{ContextLabel}] Mix stream ended", contextLabel);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[{ContextLabel}] Request cancelled", contextLabel);
                _exitTcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ContextLabel}] Error: {Message}", contextLabel, ex.Message);
                if (!_ctx.Response.HasStarted)
                {
                    try
                    {
                        _ctx.Response.StatusCode = 502;
                        await _ctx.Response.WriteAsync("Mix streamer error", cancellationToken);
                    }
                    catch (InvalidOperationException)
                    {
                        _logger.LogWarning("[{ContextLabel}] Could not set error status (response already started)", contextLabel);
                    }
                }
                _exitTcs.TrySetException(ex);
            }
            finally
            {
                try { _proc?.Dispose(); } catch { }
            }
        }

        public void Stop()
        {
            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    _logger.LogInformation("[Video+Audio Mix] Stopping ffmpeg...");
                    try
                    {
                        if (_proc.StartInfo.RedirectStandardInput)
                        {
                            _proc.StandardInput.WriteLine("q");
                            _proc.StandardInput.Flush();
                        }
                    }
                    catch { }

                    if (!_proc.WaitForExit(5000))
                    {
                        _logger.LogWarning("[Video+Audio Mix] ffmpeg did not exit, killing...");
                        _proc.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Video+Audio Mix] Error stopping ffmpeg");
            }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            try { _proc?.Dispose(); } catch { }
        }

        private static string BuildFfmpegArgs(string videoSource, string audioSource, bool audioEnabled)
        {
            var args = new List<string>
            {
                "-hide_banner",
                "-nostats",
                "-loglevel error",
                "-nostdin",
                "-fflags nobuffer",
                
                // Video input
                "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2",
                "-fflags +genpts+discardcorrupt",
                "-analyzeduration 5M -probesize 10M",
                $"-i \"{videoSource}\""
            };

            // Audio input (if enabled)
            if (audioEnabled)
            {
                args.Add("-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2");
                args.Add("-fflags +genpts+discardcorrupt");
                args.Add($"-i \"{audioSource}\"");
            }

            // Video encoding
            args.Add("-c:v libx264");
            args.Add("-preset ultrafast");
            args.Add("-b:v 2500k");
            args.Add("-maxrate 3000k");
            args.Add("-bufsize 6000k");
            args.Add("-pix_fmt yuv420p");
            args.Add("-g 60");  // GOP size

            // Audio encoding (if enabled)
            if (audioEnabled)
            {
                args.Add("-c:a aac");
                args.Add("-b:a 128k");
                args.Add("-ar 44100");
            }
            else
            {
                args.Add("-an");
            }

            // Output format
            args.Add("-f mp4");
            args.Add("-movflags +frag_keyframe+empty_moov");
            args.Add("pipe:1");

            return string.Join(" ", args);
        }
    }
}
```

**Location:** Create new file at `Streamers/MixStreamer.cs`

**Testing:**
```bash
# Verify file compiles
dotnet build
# Check compilation
dotnet build Streamers/MixStreamer.cs 2>&1 | grep -i error
# Should produce no errors
```

---

### Change 2.2: Register `/stream/mix` Route

**File:** `Program.cs`

**Location:** After `/stream/overlay` route (around line 2920)

**Change:**
```csharp
// Add this route after the overlay route
app.MapGet("/stream/mix", async (HttpContext ctx) =>
{
    try
    {
        var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
        var logger = ctx.RequestServices.GetRequiredService<ILogger<MixStreamer>>();
        var streamer = new MixStreamer(config, ctx, logger);
        await streamer.StartAsync(ctx.RequestAborted);
    }
    catch (Exception ex)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error in /stream/mix handler");
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync("Mix endpoint error");
        }
    }
});
```

**Testing:**
```bash
# Test mix endpoint
timeout 3 curl http://localhost:8080/stream/mix -o test_mix.mp4
# Should download MP4 file (or timeout after 3 seconds)
file test_mix.mp4
# Should identify as MP4 file
```

---

### Change 2.3: Build and Verify Phase 2

**Steps:**
```bash
cd /home/ddatti/printstreamer
dotnet build
```

**Verification:**
- ✓ Build succeeds
- ✓ `/stream/mix` endpoint responds
- ✓ MixStreamer logs appear in console
- ✓ ffmpeg process starts and processes streams

---

## Phase 3: YouTube Integration (Hours 2-3)

### Change 3.1: Update FfmpegStreamer to Use `/stream/mix`

**File:** `Streamers/FfmpegStreamer.cs`

**Location:** Around line 200-220 in `BuildFfmpegArgs()` method

**Current Code:**
```csharp
private static string BuildFfmpegArgs(string source, string? rtmpUrl, int fps, int bitrateKbps, FfmpegOverlayOptions? overlay, string? audioUrl)
{
    // ... existing code ...
    var srcArg = source;
    var inputFormat = "";
    if (source.StartsWith("/dev/") || source.StartsWith("/dev\\"))
    {
        inputFormat = "-f v4l2 ";
    }
    // ... continues ...
}
```

**Change:**
```csharp
private static string BuildFfmpegArgs(string source, string? rtmpUrl, int fps, int bitrateKbps, FfmpegOverlayOptions? overlay, string? audioUrl)
{
    // IMPORTANT: If source is /stream/overlay, it's already mixed with audio
    // from the MixStreamer, so we should read it as a complete MP4 instead
    
    var isPreMixed = source.Contains("/stream/mix", StringComparison.OrdinalIgnoreCase);
    
    if (isPreMixed)
    {
        // Source is already mixed video+audio from /stream/mix
        // Just encode for RTMP output, don't process audio separately
        
        var srcArg = source;
        var args = string.Join(" ", new[]
        {
            "-hide_banner -nostats -loglevel error -nostdin -fflags nobuffer",
            "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2",
            "-fflags +genpts+discardcorrupt",
            $"-i \"{srcArg}\"",
            "-c:v libx264 -preset ultrafast -b:v 3000k -maxrate 4000k -bufsize 8000k",
            "-c:a aac -b:a 128k",
            $"-f flv \"{rtmpUrl}\""
        });
        return args;
    }

    // Fallback to old behavior for non-mixed sources (backward compatibility)
    // ... existing code continues ...
}
```

**Testing:**
```bash
# This change requires YouTube setup to test properly
# For now, just verify build succeeds
dotnet build
```

---

### Change 3.2: Update StreamService to Use `/stream/mix`

**File:** `Services/StreamService.cs`

**Location:** Around line 85-90

**Current Code:**
```csharp
var source = _config.GetValue<string>("Stream:Source");
var serveEnabled = _config.GetValue<bool?>("Serve:Enabled") ?? true;
if (serveEnabled)
{
    source = "http://127.0.0.1:8080/stream/overlay";
    _logger.LogInformation("[StreamService] Using local overlay stream as ffmpeg source (http://127.0.0.1:8080/stream/overlay)");
}
```

**Change:**
```csharp
var source = _config.GetValue<string>("Stream:Source");
var serveEnabled = _config.GetValue<bool?>("Serve:Enabled") ?? true;
if (serveEnabled)
{
    // Use the pre-mixed /stream/mix endpoint which combines video+audio
    // This allows multiple outputs (YouTube, local recording) to consume the same mix
    source = "http://127.0.0.1:8080/stream/mix";
    _logger.LogInformation("[StreamService] Using local mixed stream as ffmpeg source (http://127.0.0.1:8080/stream/mix)");
}
```

**Testing:**
```bash
# Verify startup
dotnet run --configuration Debug
# Check logs for "Using local mixed stream" message
```

---

### Change 3.3: Remove Redundant Audio Handling in FfmpegStreamer

**File:** `Streamers/FfmpegStreamer.cs`

**Location:** Around line 165-200

**Current Code:**
```csharp
public FfmpegStreamer(string source, string? rtmpUrl, int targetFps, int bitrateKbps, FfmpegOverlayOptions? overlay, string? audioUrl, ILogger<FfmpegStreamer> logger)
{
    _source = source;
    _rtmpUrl = rtmpUrl;
    _targetFps = targetFps <= 0 ? 30 : targetFps;
    _bitrateKbps = bitrateKbps <= 0 ? 800 : bitrateKbps;
    _overlay = overlay;
    _audioUrl = audioUrl;  // ← This becomes redundant
    _logger = logger;
    // ...
}
```

**Change (Optional - Backward Compatible):**
```csharp
public FfmpegStreamer(string source, string? rtmpUrl, int targetFps, int bitrateKbps, FfmpegOverlayOptions? overlay, string? audioUrl, ILogger<FfmpegStreamer> logger)
{
    _source = source;
    _rtmpUrl = rtmpUrl;
    _targetFps = targetFps <= 0 ? 30 : targetFps;
    _bitrateKbps = bitrateKbps <= 0 ? 800 : bitrateKbps;
    _overlay = overlay;
    // Audio is now handled by MixStreamer, but keep parameter for backward compatibility
    _audioUrl = null;  // Ignore audioUrl parameter, use /stream/mix instead
    _logger = logger;
    // ...
}
```

**Note:** This is optional - can keep the old parameter for backward compatibility.

---

### Change 3.4: Build and Verify Phase 3

**Steps:**
```bash
cd /home/ddatti/printstreamer
dotnet build
dotnet run
```

**Verification:**
- ✓ Build succeeds
- ✓ Application starts
- ✓ StreamService logs show "Using local mixed stream"
- ✓ YouTube broadcast works (if configured)

---

## Phase 4: Cleanup & Documentation (Hour 3+)

### Change 4.1: Update Logging Messages

Add debug logging to trace data flow:

**File:** `Program.cs`

**Change:**
```csharp
// Add after all route definitions
if (serveEnabled)
{
    logger.LogInformation("=== DATA FLOW PIPELINE ===");
    logger.LogInformation("Stage 1: /stream/source (raw webcam)");
    logger.LogInformation("Stage 2: /stream/overlay (video + overlays)");
    logger.LogInformation("Stage 3: /stream/audio (MP3 audio)");
    logger.LogInformation("Stage 4: /stream/mix (H.264 + AAC)");
    logger.LogInformation("Stage 5: RTMP to YouTube");
    logger.LogInformation("=== END PIPELINE ===");
}
```

---

### Change 4.2: Add Debugging Endpoints (Optional)

**File:** `Program.cs`

**Change:**
```csharp
// Add diagnostic endpoint to check pipeline status
app.MapGet("/api/debug/pipeline", (HttpContext ctx) =>
{
    var sources = new Dictionary<string, string>
    {
        ["webcam_source"] = "http://127.0.0.1:8080/stream/source",
        ["overlay_video"] = "http://127.0.0.1:8080/stream/overlay",
        ["audio_stream"] = "http://127.0.0.1:8080/stream/audio",
        ["mixed_output"] = "http://127.0.0.1:8080/stream/mix"
    };
    return Results.Json(sources);
});
```

**Usage:**
```bash
curl http://localhost:8080/api/debug/pipeline
# Returns:
# {
#   "webcam_source": "http://127.0.0.1:8080/stream/source",
#   "overlay_video": "http://127.0.0.1:8080/stream/overlay",
#   "audio_stream": "http://127.0.0.1:8080/stream/audio",
#   "mixed_output": "http://127.0.0.1:8080/stream/mix"
# }
```

---

### Change 4.3: Update Configuration Example

**File:** `appsettings.json` (optional addition)

**Change:**
```json
{
  "_comment": "Data flow pipeline endpoints (read-only, for debugging)",
  "Pipeline": {
    "Endpoints": {
      "WebcamSource": "http://127.0.0.1:8080/stream/source",
      "OverlayVideo": "http://127.0.0.1:8080/stream/overlay",
      "AudioStream": "http://127.0.0.1:8080/stream/audio",
      "MixedOutput": "http://127.0.0.1:8080/stream/mix"
    }
  }
}
```

---

### Change 4.4: Update README or Architecture Docs

Add section to DATAFLOW_ARCHITECTURE.md or create a new TROUBLESHOOTING.md:

```markdown
## Testing the Pipeline

### Test each stage independently:

1. **Webcam Source**
   ```bash
   curl http://localhost:8080/stream/source -o test_source.mjpeg
   ffplay test_source.mjpeg
   ```

2. **Overlay Video**
   ```bash
   curl http://localhost:8080/stream/overlay -o test_overlay.mjpeg
   ffplay test_overlay.mjpeg
   ```

3. **Audio Stream**
   ```bash
   curl http://localhost:8080/stream/audio -o test_audio.mp3
   ffplay test_audio.mp3
   ```

4. **Mixed Output**
   ```bash
   timeout 5 curl http://localhost:8080/stream/mix -o test_mix.mp4
   ffplay test_mix.mp4
   ```

### Common Issues

- **Mix endpoint times out**: Check if /stream/overlay or /stream/audio are responding
- **No audio in mix**: Verify Audio:Enabled=true in appsettings.json
- **Overlay text missing**: Check OverlayTextService logs
- **YouTube broadcast fails**: Verify /stream/mix is working first
```

---

## Testing Strategy

### Unit Tests (No changes needed)
- Existing tests should pass
- New MixStreamer can have unit tests added later

### Integration Tests

**Manual Test Cases:**

1. **Pipeline Availability**
   ```bash
   for endpoint in source overlay audio mix; do
     echo "Testing /stream/$endpoint"
     timeout 2 curl -s http://localhost:8080/stream/$endpoint > /dev/null && echo "✓ OK" || echo "✗ FAIL"
   done
   ```

2. **Fallback Behavior**
   ```bash
   # Disable camera
   curl -X POST http://localhost:8080/api/camera/off
   # Check /stream/source returns black image
   curl http://localhost:8080/stream/source -o fallback.mjpeg
   # Check /stream/overlay responds with black image fallback
   curl http://localhost:8080/stream/overlay -o overlay_fallback.mjpeg
   ```

3. **Audio Quality**
   ```bash
   # Queue a test audio file
   curl -X POST "http://localhost:8080/api/audio/queue?name=testtrack.mp3"
   # Check /stream/audio produces audio
   timeout 2 curl http://localhost:8080/stream/audio > audio_test.mp3
   file audio_test.mp3
   ```

4. **Video+Audio Sync**
   ```bash
   # Capture mixed stream
   timeout 10 curl http://localhost:8080/stream/mix -o sync_test.mp4
   # Verify with ffmpeg
   ffmpeg -i sync_test.mp4 -f null -
   # Check for A/V sync issues in output
   ```

5. **YouTube Broadcast**
   ```bash
   # (Requires YouTube setup)
   # Start broadcast and verify /stream/mix is the input
   # Check quality and latency
   ```

---

## Rollback Plan

If issues occur after deployment:

### Quick Rollback (Keep `/stream` alias)
```csharp
// Program.cs - keep both routes active
app.MapGet("/stream", async (HttpContext ctx) => await webcamManager.HandleStreamRequest(ctx));
app.MapGet("/stream/source", async (HttpContext ctx) => await webcamManager.HandleStreamRequest(ctx));
```

### Full Rollback (Revert to FfmpegStreamer reading `/stream/overlay`)
```csharp
// StreamService.cs
var source = "http://127.0.0.1:8080/stream/overlay";  // Not /stream/mix
```

---

## Deployment Checklist

- [ ] All files compiled successfully
- [ ] No breaking errors in logs
- [ ] `/stream/source` endpoint responds
- [ ] `/stream/audio` endpoint responds
- [ ] `/stream/overlay` endpoint responds
- [ ] `/stream/mix` endpoint responds
- [ ] YouTube broadcast works with new pipeline
- [ ] Audio sync verified
- [ ] Fallback (black image) works
- [ ] No warnings in application logs

---

## Performance Implications

### Before
```
Webcam → OverlayMjpegStreamer → FfmpegStreamer → YouTube
         (ffmpeg per request)   (1x ffmpeg)
```

### After
```
Webcam → OverlayMjpegStreamer → MixStreamer → FfmpegStreamer → YouTube
         (ffmpeg per request)   (1x ffmpeg)   (1x ffmpeg)
```

**Resource Usage Change:**
- +1 ffmpeg process (MixStreamer)
- Same CPU/memory per stage (slight increase, acceptable)
- Better isolation (one stage failure doesn't cascade)

---

## Success Criteria

✅ All phases complete when:

1. All routes respond (stages 1-4)
2. YouTube broadcast works via `/stream/mix`
3. Audio sync is maintained
4. Fallback scenarios work (camera offline)
5. No errors in logs
6. Performance acceptable (<5s latency to YouTube)
7. Can monitor each stage independently

---

**End of Implementation Plan**

For questions during implementation, refer to DATAFLOW_ARCHITECTURE.md for detailed flow descriptions.
