# Data Flow Pipeline - Quick Reference

## Architecture Overview

```
Physical Webcam
    ↓
/stream/source (MJPEG) ─── WebCamManager
    ├─ Direct camera proxy
    ├─ Fallback to black image
    │
    ├──→ /stream/source/capture (JPEG) ─── TimelapseManager
    │    ├─ Raw frame capture
    │    └─ Timelapse video compilation
    │
    ├──→ /stream/overlay (MJPEG) ─── OverlayMjpegStreamer (ffmpeg)
    │    ├─ Overlay text from printer state
    │    ├─ Scaling and formatting
    │    └─ Used for preview/mixing
    │    │
    │    └──→ /stream/overlay/capture (JPEG) ─── Manual snapshot
    │         └─ Frame with text overlays
    │
    ├──→ /stream/audio (MP3) ─── AudioBroadcastService
    │    ├─ Queue-based playback
    │    └─ Live streaming
    │
    ├──→ /stream/mix (MP4) ─── MixStreamer (ffmpeg)
    │    ├─ Combines video + audio
    │    ├─ H.264 + AAC encoding
    │    └─ Ready for broadcast
    │    │
    │    └──→ /stream/mix/capture (JPEG) ─── Future use cases
    │         └─ Frame from mixed stream
    │
    └──→ YouTube RTMP ─── FfmpegStreamer
         ├─ Detects /stream/mix
         ├─ Optimized ffmpeg command
         └─ Live broadcast
```

---

## Quick Test Guide

### 1. Test Webcam Source
```bash
# Get raw webcam stream
curl http://localhost:8080/stream/source -o source.mjpeg
ffplay source.mjpeg
```

### 2. Test Overlay Video
```bash
# Get video with printer overlays (temp, layer count, etc)
curl http://localhost:8080/stream/overlay -o overlay.mjpeg
ffplay overlay.mjpeg
```

### 2.5. Test Overlay Capture
```bash
# Get single frame with overlay text
curl http://localhost:8080/stream/overlay/capture -o overlay-frame.jpg
display overlay-frame.jpg
```

### 3. Test Audio Stream
```bash
# Get live audio from queue
timeout 5 curl http://localhost:8080/stream/audio -o audio.mp3
ffplay audio.mp3
```

### 4. Test Mix (Video+Audio)
```bash
# Get complete mixed stream
timeout 5 curl http://localhost:8080/stream/mix -o mix.mp4
ffplay mix.mp4
```

### 4.5. Test Mix Capture
```bash
# Get single frame from mixed stream
curl http://localhost:8080/stream/mix/capture -o mix-frame.jpg
display mix-frame.jpg
```

### 1.5. Test Source Capture (Timelapse)
```bash
# Get single JPEG frame from raw source
curl http://localhost:8080/stream/source/capture -o frame.jpg
display frame.jpg
# or
ffplay frame.jpg
```

### 5. Test Debug Endpoint
```bash
# Check pipeline health
curl http://localhost:8080/api/debug/pipeline | jq
```

---

## Configuration Keys

| Key | Default | Purpose |
|-----|---------|---------|
| `Stream:Source` | (required) | Physical camera source (HTTP URL or /dev/video0) |
| `Serve:Enabled` | `true` | Enable local serving (uses /stream/mix) |
| `Audio:Enabled` | `true` | Enable audio in mix |
| `Overlay:StreamSource` | `http://127.0.0.1:8080/stream/source` | Source for overlay pipeline |
| `Timelapse:CaptureInterval` | 60000ms | Interval for timelapse frame capture |
| `Timelapse:Enabled` | `true` | Enable timelapse recording |

---

## Key Files

| File | Purpose |
|------|---------|
| `Program.cs` | Route definitions for all 4 stages + debug |
| `Streamers/MixStreamer.cs` | Stage 4 implementation |
| `Streamers/OverlayMjpegStreamer.cs` | Stage 2 implementation |
| `Services/StreamService.cs` | Main service orchestrating the flow |
| `DATAFLOW_ARCHITECTURE.md` | Complete architectural documentation |
| `IMPLEMENTATION_PLAN.md` | Detailed implementation steps |

---

## Troubleshooting

### /stream/source returns 404
- **Cause**: WebCamManager not initialized
- **Fix**: Ensure `Stream:Source` is configured

### /stream/overlay returns 502
- **Cause**: ffmpeg process crashed or ffmpeg not installed
- **Fix**: Check logs for ffmpeg errors; verify ffmpeg is in PATH

### /stream/audio returns 404
- **Cause**: Audio:Enabled = false or no audio queued
- **Fix**: Enable Audio:Enabled and queue tracks

### /stream/mix returns 502
- **Cause**: Overlay or audio endpoint not responding
- **Fix**: Test /stream/overlay and /stream/audio first

### YouTube broadcast not working
- **Cause**: /stream/mix may be failing or RTMP credentials invalid
- **Fix**: Test /stream/mix first; verify YouTube RTMP settings

---

## Performance Notes

### Typical Resource Usage
- **CPU**: 40-60% for full pipeline (4 ffmpeg processes)
- **Memory**: 400-600MB total
- **Network**: ~5-8 Mbps for 1080p@30fps with audio
- **Latency**: 500-2000ms end-to-end (YouTube adds ~5-30s)

### Optimization Tips
- Reduce bitrate if CPU usage too high
- Use ultrafast preset (already set) for low latency
- Monitor `/stream/mix` quality independently

---

## Data Flow Stages

### Stage 1: Source (0ms latency)
- Direct camera proxy
- Fallback to black image if offline
- Multiple consumers allowed

### Stage 1.5: Source Capture (parallel)
- Single JPEG from raw camera
- Used by TimelapseManager
- ~10s timeout per frame

### Stage 2: Overlay (50-100ms latency)
- Adds text overlays (temp, ETA, etc)
- Scaling and formatting
- Per-request ffmpeg process

### Stage 2.5: Overlay Capture (parallel)
- Single JPEG with overlays
- Manual snapshot requests
- ~10s timeout per frame

### Stage 3: Audio (depends on queue)
- Live audio playback from queue
- Seamless track transitions
- Shared broadcast service

### Stage 4: Mix (100-200ms latency)
- Combines video + audio
- H.264 + AAC encoding
- MP4 container format

### Stage 4.5: Mix Capture (parallel)
- Single JPEG from mixed stream
- Future use cases
- ~10s timeout per frame

### Stage 5: YouTube RTMP (optimized)
- Reads pre-mixed stream
- Optional re-encoding
- Direct RTMP output

### Parallel: Timelapse Capture
- Uses /stream/source/capture
- Single frame snapshots
- Timer-based (~1 minute intervals)
- Stored per-session in /data/timelapse/
- Compiled to video on session completion

---

## Example Curl Commands

```bash
# Check pipeline endpoints
curl -s http://localhost:8080/api/debug/pipeline | jq '.' 

# Get WebCam status
curl -s http://localhost:8080/api/camera | jq '.'

# Get Audio broadcast status
curl -s http://localhost:8080/api/audio/broadcast/status | jq '.'

# Queue audio track
curl -X POST "http://localhost:8080/api/audio/queue?name=track.mp3"

# Get stream service status
curl -s http://localhost:8080/api/live/status | jq '.'
```

---

## Build & Run

```bash
# Build all phases
dotnet build

# Run application
dotnet run --configuration Release

# Or with specific settings
dotnet run --configuration Release -- --Stream:Source="http://camera.local/stream"
```

---

**Last Updated:** November 7, 2025  
**Status:** ✅ Implementation Complete & Building Successfully
