# PrintStreamer Complete Endpoint Reference

**Last Updated:** November 7, 2025

## Streaming Pipeline Endpoints

### Stage 1: Source Buffering
```
GET /stream/source
├─ Returns: MJPEG stream (multipart/x-mixed-replace)
├─ Source: Raw webcam (WebCamManager)
├─ Consumers: Overlay stage, capture, direct viewers
└─ Fallback: Black JPEG when offline
```

**Capture Point:**
```
GET /stream/source/capture
├─ Returns: Single JPEG image
├─ Format: image/jpeg
├─ Purpose: Timelapse frame capture (default)
├─ Timeout: 10 seconds
└─ Used by: TimelapseManager
```

---

### Stage 3: Overlay Buffering
```
GET /stream/overlay
├─ Returns: MJPEG stream (multipart/x-mixed-replace)
├─ Source: /stream/source + text overlays
├─ Overlays: Temperature, layer count, progress, ETA
├─ Processing: ffmpeg with drawbox + drawtext filters
├─ Consumers: Mix stage, direct viewers, capture
└─ Output: Scaled 1920x1080, high quality MJPEG
```

**Capture Point:**
```
GET /stream/overlay/capture
├─ Returns: Single JPEG image
├─ Format: image/jpeg
├─ Purpose: Manual snapshot with text overlays
├─ Timeout: 10 seconds
└─ Quality: Includes all overlay text
```

---

### Stage 4: Audio Buffering
```
GET /stream/audio
├─ Returns: MP3 audio stream
├─ Source: Audio files from /data/audio/ directory
├─ Playback: Queue-based (managed by AudioService)
├─ Processing: ffmpeg encoding to MP3 or pass-through
├─ Consumers: Mix stage
└─ Seamless: Transitions between tracks without gaps
```

---

### Stage 5: Mix Buffering
```
GET /stream/mix
├─ Returns: MP4 stream (H.264 video + AAC audio)
├─ Inputs: /stream/overlay (video) + /stream/audio (audio)
├─ Video Codec: H.264 (libx264 ultrafast)
├─ Audio Codec: AAC
├─ Processing: Synchronized audio-video mixing via ffmpeg
├─ Consumers: YouTube RTMP, direct recording, capture
└─ Output: Bandwidth-optimized for low-latency broadcast
```

**Capture Point:**
```
GET /stream/mix/capture
├─ Returns: Single JPEG image
├─ Format: image/jpeg
├─ Purpose: Frame capture from mixed stream (future use)
├─ Timeout: 10 seconds
└─ Use Case: Debugging, frame verification, future features
```

---

### YouTube Broadcast
```
GET /stream/mix → POST rtmp://a.rtmp.youtube.com/live2/{STREAM_KEY}
├─ Input: /stream/mix (H.264 + AAC)
├─ Processing: Optional re-encoding (if needed for YouTube specs)
├─ Output: RTMP stream to YouTube Live
├─ Live: Immediately available to viewers
├─ Recording: Saved to YouTube channel
└─ Processor: FfmpegStreamer
```

---

## Capture Endpoints Summary

| Endpoint | Source | Format | Purpose | Timeout |
|----------|--------|--------|---------|---------|
| `/stream/source/capture` | Raw webcam | JPEG | Timelapse frames (default) | 10s |
| `/stream/overlay/capture` | /stream/overlay | JPEG | Manual snapshots with text | 10s |
| `/stream/mix/capture` | /stream/mix | JPEG | Mixed stream frame capture | 10s |

---

## Timelapse Data Flow

```
/stream/source/capture (JPEG)
    ↓
TimelapseManager
    ↓
Frame Storage: /data/timelapse/<session-id>/frame-NNNN.jpg
    ↓
On Session End: ffmpeg compilation
    ↓
Output: /data/timelapse/<session-id>/timelapse.mp4
```

### Default Configuration
- **Capture Endpoint:** `http://127.0.0.1:8080/stream/source/capture`
- **Capture Interval:** ~1 minute (configurable)
- **Frame Format:** JPEG
- **Storage:** Per-session directories
- **Compilation:** 30fps H.264 video

---

## Diagnostic Endpoints

```
GET /api/debug/pipeline
├─ Returns: JSON status of all pipeline stages
├─ Shows: Active processes, stream health, configuration
└─ Purpose: Pipeline health monitoring
```

---

## Implementation Notes

### Capture Endpoint Implementation
- Shared `CaptureJpegFromStreamAsync()` helper function in Program.cs
- Attempts `?action=snapshot` parameter first (camera native snapshot)
- Falls back to JPEG parsing from MJPEG stream using SOI/EOI markers
- 10-second timeout with proper error handling
- No-cache headers to ensure fresh frames

### Error Handling
- **200 OK:** Frame successfully captured
- **503 Service Unavailable:** Stream source not available
- **504 Gateway Timeout:** Capture exceeded timeout
- **502 Bad Gateway:** Other capture errors

### Performance Characteristics
- `/stream/source/capture`: ~50-200ms (raw camera, fastest)
- `/stream/overlay/capture`: ~100-500ms (includes overlay processing)
- `/stream/mix/capture`: ~200-1000ms (from encoded H.264 stream)

---

## Configuration Reference

Key configuration values in `appsettings.json`:

```json
{
  "Stream:Source": "http://localhost:8081/webcam/?action=snapshot",
  "Overlay:StreamSource": "http://127.0.0.1:8080/stream/source",
  "Audio:Enabled": true,
  "Moonraker:BaseUrl": "http://localhost:7125",
  "YouTube:RtmpUrl": "rtmp://a.rtmp.youtube.com/live2",
  "YouTube:StreamKey": "YOUR_STREAM_KEY_HERE"
}
```

---

## Testing Quick Reference

```bash
# Test source streaming
curl http://localhost:8080/stream/source -o source.mjpeg
ffplay source.mjpeg

# Test source capture (timelapse frames)
curl http://localhost:8080/stream/source/capture -o raw.jpg
file raw.jpg

# Test overlay streaming
curl http://localhost:8080/stream/overlay -o overlay.mjpeg
ffplay overlay.mjpeg

# Test overlay capture
curl http://localhost:8080/stream/overlay/capture -o overlay.jpg
file overlay.jpg

# Test audio
timeout 5 curl http://localhost:8080/stream/audio -o audio.mp3
ffplay audio.mp3

# Test mix
timeout 5 curl http://localhost:8080/stream/mix -o mix.mp4
ffplay mix.mp4

# Test mix capture
curl http://localhost:8080/stream/mix/capture -o mix.jpg
file mix.jpg

# Check pipeline health
curl http://localhost:8080/api/debug/pipeline | jq
```

---

## Data Flow Diagram

```
┌────────────────────────────────────────────────────────────────────┐
│                    PRINTSTREAMER ENDPOINT MAP                      │
└────────────────────────────────────────────────────────────────────┘

Physical Camera (HTTP/V4L2/USB)
        │
        ↓
    WebCamManager
        │
        ├─→ /stream/source (MJPEG)
        │       │
        │       ├─→ /stream/source/capture (JPEG) ────→ Timelapse
        │       │
        │       └─→ OverlayMjpegStreamer (ffmpeg)
        │               │
        │               ├─→ /stream/overlay (MJPEG)
        │               │       │
        │               │       └─→ /stream/overlay/capture (JPEG)
        │               │
        │               ├─ Overlay Text: /tmp/overlay_text
        │               │   (from Moonraker API)
        │               │
        │               └─→ MixStreamer (ffmpeg)
        │                       │
        │                       ├─→ AudioService
        │                       │   └─→ /stream/audio (MP3)
        │                       │
        │                       └─→ /stream/mix (H.264+AAC)
        │                               │
        │                               ├─→ /stream/mix/capture (JPEG)
        │                               │
        │                               └─→ FfmpegStreamer
        │                                   └─→ YouTube RTMP
        │
        └─→ /fallback_black.jpg (offline fallback)
```

---

**Status:** ✅ Complete and Tested  
**Build:** ✅ Successful  
**Documentation:** ✅ Comprehensive
