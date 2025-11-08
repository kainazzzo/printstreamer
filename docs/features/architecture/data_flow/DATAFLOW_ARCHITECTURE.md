# PrintStreamer Data Flow Architecture

**Version:** 1.0  
**Date:** November 7, 2025  
**Status:** Proposal & Documentation

---

## Table of Contents

1. [Overview](#overview)
2. [Current Architecture](#current-architecture)
3. [Proposed Architecture](#proposed-architecture)
4. [Data Flow Stages](#data-flow-stages)
5. [Endpoint Mappings](#endpoint-mappings)
6. [Implementation Details](#implementation-details)
7. [Configuration](#configuration)
8. [Changes Required](#changes-required)

---

## Overview

PrintStreamer processes video and audio from multiple sources and combines them into a single composited stream that can be broadcast to YouTube Live or served locally. The architecture uses a **multi-stage pipeline** where each stage buffers its output into an HTTP endpoint for downstream consumption.

### Design Principles

- **Decoupling:** Each stage is independent and can be tested/debugged separately
- **Buffering:** HTTP endpoints provide natural buffering and allow multiple consumers
- **Resilience:** If any stage fails, downstream stages can fall back gracefully
- **Simplicity:** Clear data flow makes the system easier to understand and maintain

---

## Current Architecture

### Current State (Before Proposed Changes)

```
Webcam Source (HTTP/V4L2)
    ↓
WebCamManager (handles camera simulation + fallback)
    ↓
/stream endpoint (MJPEG)
    ↓
[Two Parallel Paths]
    ├─→ OverlayMjpegStreamer
    │   ├─ Input: /stream (raw MJPEG)
    │   ├─ Filter: drawbox + drawtext overlays (ffmpeg)
    │   └─ Output: /stream/overlay (overlayed MJPEG)
    │
    └─→ FfmpegStreamer
        ├─ Input: /stream/overlay (overlayed MJPEG) + /api/audio/stream (MP3)
        ├─ Process: Mix video + audio, encode h.264
        └─ Output: 
            ├─ Local file (buffer)
            └─ RTMP to YouTube

Audio Service (from filesystem)
    ├─ Scans audio folder
    ├─ Queues tracks
    └─ Plays back via AudioBroadcastService
        └─ Streams to /api/audio/stream
```

**Problems with Current Architecture:**

1. ✗ No dedicated **webcam source endpoint** - WebCamManager is tightly coupled to /stream
2. ✗ Audio path is **implicit** - only accessed inside FfmpegStreamer via URL
3. ✗ No dedicated **overlay buffer** - OverlayMjpegStreamer creates on-demand per request
4. ✗ No dedicated **mix endpoint** - FfmpegStreamer combines video+audio but doesn't expose output
5. ✗ Multiple FfmpegStreamer instances can't share the same mix
6. ✗ YouTube output is **tightly coupled** to FfmpegStreamer

---

## Proposed Architecture

### New Data Flow (Recommended)

```
┌─────────────────────────────────────────────────────────────────────┐
│                     PRINTSTREAMER DATA FLOW                         │
└─────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│ STAGE 1: WEBCAM SOURCE BUFFERING                                     │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  Physical Webcam Source                                             │
│  (HTTP stream, V4L2 device, or USB camera)                          │
│         ↓                                                            │
│  WebCamManager (proxy + simulation)                                 │
│  • Handles camera on/off simulation                                 │
│  • Serves fallback_black.jpg when offline                          │
│  • MJPEG buffering and client management                           │
│         ↓                                                            │
│  [/stream/source] ← ENDPOINT 1: Raw Webcam MJPEG                   │
│  • MJPEG multipart/x-mixed-replace stream                          │
│  • Multiple consumers allowed                                       │
│  • Fallback to black image + text when camera offline              │
│                                                                      │
│  Consumers: Overlay Pipeline, Direct viewers, Recording            │
└──────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│ STAGE 2: OVERLAY TEXT BUFFERING                                      │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  [/stream/source] (from Stage 1)                                    │
│         ↓                                                            │
│  OverlayTextService                                                 │
│  • Reads printer state from Moonraker API                          │
│  • Generates overlay text (nozzle temp, layer, progress, ETA)      │
│  • Updates text file periodically (~500ms default)                 │
│         ↓                                                            │
│  Text File (temporary): /tmp/overlay_text                          │
│  • Updated in real-time as printer state changes                   │
│                                                                      │
│  Consumers: OverlayMjpegStreamer, FfmpegStreamer                   │
└──────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│ STAGE 3: OVERLAY MJPEG BUFFERING                                     │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  [/stream/source] (from Stage 1)                                    │
│       +                                                             │
│  Overlay Text (from Stage 2)                                       │
│       ↓                                                              │
│  OverlayMjpegStreamer (ffmpeg process)                            │
│  • Input: /stream/source (raw MJPEG)                              │
│  • Filters:                                                         │
│    - scale=1920:1080 (upscale to HD)                              │
│    - pad=1920:1080 (center with black background)                 │
│    - drawbox (banner background)                                   │
│    - drawtext (overlay text from file)                            │
│  • Output: MJPEG with high quality                                │
│         ↓                                                            │
│  [/stream/overlay] ← ENDPOINT 2: Overlayed MJPEG                   │
│  • MJPEG multipart/x-mixed-replace stream                          │
│  • Contains all overlays and text                                  │
│  • Ready for consumption by mix pipeline                           │
│                                                                      │
│  Consumers: Mix Pipeline, Direct viewers, Recording               │
└──────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│ STAGE 4: AUDIO BUFFERING                                             │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  Audio Service (Singleton)                                         │
│  • Scans audio folder for MP3/WAV/FLAC files                      │
│  • Maintains queue of tracks to play                               │
│         ↓                                                            │
│  AudioBroadcastService (ffmpeg process or direct playback)        │
│  • Reads current track from AudioService queue                     │
│  • Encodes to MP3 (if needed) or passes through                   │
│  • Buffers audio stream for live consumption                       │
│         ↓                                                            │
│  [/stream/audio] ← ENDPOINT 3: Buffered MP3 Audio                  │
│  • MP3 stream format                                               │
│  • Live audio from current queue                                   │
│  • Seamless track transitions                                      │
│                                                                      │
│  Consumers: Mix Pipeline, Direct audio players                     │
└──────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│ STAGE 5: MIX (VIDEO + AUDIO) BUFFERING                               │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  [/stream/overlay] (from Stage 3)                                   │
│       +                                                             │
│  [/stream/audio] (from Stage 4)                                     │
│       ↓                                                              │
│  MixMjpegStreamer (ffmpeg process)                                │
│  • Input 1: /stream/overlay (video with text overlay)             │
│  • Input 2: /stream/audio (MP3 audio)                             │
│  • Operations:                                                      │
│    - Decode MJPEG video                                            │
│    - Decode MP3 audio                                              │
│    - Synchronize A/V timing                                        │
│    - Mix audio (if multiple sources)                               │
│    - Re-encode to H.264 video + AAC audio                         │
│  • Output: MP4 with embedded audio                                │
│         ↓                                                            │
│  [/stream/mix] ← ENDPOINT 4: Composited Video + Audio              │
│  • MP4 format (or MJPEG with audio metadata)                       │
│  • Ready for broadcast or recording                                │
│  • Bandwidth-optimized encoding                                    │
│                                                                      │
│  Consumers: YouTube RTMP broadcaster, Local recording, Viewers     │
└──────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│ STAGE 6: YOUTUBE BROADCAST                                           │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  [/stream/mix] (from Stage 5)                                       │
│       ↓                                                              │
│  YouTubeBroadcaster (ffmpeg process)                              │
│  • Input: /stream/mix (composited video+audio)                    │
│  • Decode: H.264 video + AAC audio                                │
│  • Operations:                                                      │
│    - Re-encode if needed for YouTube specs                         │
│    - Enforce bitrate/resolution limits                             │
│    - Add metadata (title, description, privacy)                    │
│  • Output: RTMP stream to YouTube servers                         │
│         ↓                                                            │
│  YouTube Live (Public/Unlisted/Private)                            │
│  • Real-time broadcast to viewers                                  │
│  • Can be watched immediately                                      │
│  • Recording saved to YouTube channel                              │
│                                                                      │
│  Side Effects:                                                      │
│  • Save broadcast ID for later reference                           │
│  • Transition from "testing" to "live" state                       │
│  • Enable live chat if configured                                  │
└──────────────────────────────────────────────────────────────────────┘
```

└──────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│ STAGE 0.5: CAPTURE (PARALLEL CONSUMER)                              │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  Alternative consumer path for timelapse video generation          │
│  Available at each pipeline stage for frame snapshots               │
│                                                                      │
│  Option A: Raw Source Capture                                       │
│  [/stream/source] (from Stage 1)                                    │
│       ↓                                                              │
│  [/stream/source/capture] ← Single JPEG from raw webcam            │
│  • Fastest capture (no overlay processing)                          │
│  • Lowest quality (no text overlays)                                │
│  • Direct from camera source                                        │
│                                                                      │
│  Option B: Overlay Capture                                          │
│  [/stream/overlay] (from Stage 3)                                   │
│       ↓                                                              │
│  [/stream/overlay/capture] ← Single JPEG with overlays             │
│  • Includes printer state text (temp, layer, progress, ETA)        │
│  • Higher quality (formatted overlayed image)                       │
│  • Best for visual documentation                                    │
│                                                                      │
│  Option C: Mix Capture                                              │
│  [/stream/mix] (from Stage 5)                                       │
│       ↓                                                              │
│  [/stream/mix/capture] ← Single JPEG from mixed stream             │
│  • Captures frame after audio/video mix (if ever needed)           │
│  • For special use cases (future feature)                          │
│                                                                      │
│  Default Timelapse Path:                                            │
│  TimelapseManager uses /stream/source/capture for frame capture    │
│  • Periodic capture on timer (~1 minute intervals)                 │
│  • Active for each running timelapse session                        │
│  • Captures individual JPEG frames                                  │
│         ↓                                                            │
│  Frame Storage (per session)                                        │
│  • Saved to: /data/timelapse/<session-id>/<frame-nnnn>.jpg        │
│  • Metadata: frame timestamp, layer count, print progress          │
│                                                                      │
│  Video Compilation                                                 │
│  • When session ends: compile frames into video                    │
│  • FFmpeg: framerate 30fps (configurable), quality 90-95%          │
│  • Output: /data/timelapse/<session-id>/timelapse.mp4             │
│                                                                      │
│  Consumers: Video files (local storage, web download)             │
└──────────────────────────────────────────────────────────────────────┘
```

---

## Endpoint Mappings

| Stage | Endpoint | Input | Output | Format | Consumers |
|-------|----------|-------|--------|--------|-----------|
| 1 | `/stream/source` | Webcam HTTP/V4L2 | Raw MJPEG | multipart/x-mixed-replace | Overlay, Capture |
| 1 | `/stream/source/capture` | /stream/source | Single JPEG | image/jpeg | Timelapse |
| 2 | (text file) | Moonraker API | Overlay text | text/plain | Overlay processor |
| 3 | `/stream/overlay` | /stream/source + text | Overlayed MJPEG | multipart/x-mixed-replace | Mix, Capture, Direct |
| 3 | `/stream/overlay/capture` | /stream/overlay | Single JPEG | image/jpeg | Manual snapshot |
| 4 | `/stream/audio` | Audio files (MP3/WAV) | Encoded audio | audio/mp3 | Mix |
| 5 | `/stream/mix` | /stream/overlay + /stream/audio | H.264 video + AAC audio | video/mp4 | YouTube, Direct, Capture |
| 5 | `/stream/mix/capture` | /stream/mix | Single JPEG | image/jpeg | Future use cases |

---

## Endpoint Details

### 1. `/stream/source` (Stage 1: Raw Webcam)
- **Purpose:** Buffer raw webcam MJPEG stream
- **Implementation:** WebCamManager singleton
- **URL:** `http://127.0.0.1:8080/stream/source`
- **Format:** MJPEG (multipart/x-mixed-replace)
- **Fallback:** Returns fallback_black.jpg (black image) when camera is offline
- **Consumers:** OverlayMjpegStreamer, Direct viewers

### 2. `/stream/overlay` (Stage 3: Overlayed MJPEG)
- **Purpose:** Buffer MJPEG with text overlay applied
- **Implementation:** OverlayMjpegStreamer (ffmpeg-based)
- **URL:** `http://127.0.0.1:8080/stream/overlay`
- **Input:** /stream/source + dynamic text overlay
- **Format:** MJPEG (multipart/x-mixed-replace)
- **Overlay Text:** Generated from Moonraker API data (temp, layer, progress, ETA)
- **Consumers:** MixStreamer, Direct viewers

### 3. `/stream/audio` (Stage 4: Buffered Audio)
- **Purpose:** Stream audio from playlist queue
- **Implementation:** AudioBroadcastService (ffmpeg or direct pass-through)
- **URL:** `http://127.0.0.1:8080/stream/audio`
- **Format:** MP3 audio stream
- **Source:** /data/audio/ directory (scanned for MP3/WAV/FLAC files)
- **Queue Management:** AudioService maintains play order and current track
- **Consumers:** MixStreamer

### 4. `/stream/mix` (Stage 5: Mixed Video + Audio)
- **Purpose:** Combine /stream/overlay video with /stream/audio into single stream
- **Implementation:** MixStreamer (ffmpeg H.264 + AAC)
- **URL:** `http://127.0.0.1:8080/stream/mix`
- **Input:** /stream/overlay (video) + /stream/audio (audio)
- **Format:** MP4 with H.264 video (libx264 ultrafast profile) + AAC audio
- **Encoding:** Optimized for low-latency YouTube broadcast
- **Consumers:** FfmpegStreamer (YouTube RTMP), Direct recording

### 0.5. `/stream/source/capture` (Parallel Stage 1: Raw JPEG Capture)
- **Purpose:** Serve single JPEG frame from raw camera for timelapse frame capture
- **Implementation:** Inline in Program.cs, reads from /stream/source
- **URL:** `http://127.0.0.1:8080/stream/source/capture`
- **Format:** Single image/jpeg (not streaming)
- **Method:** Attempts snapshot endpoint first, falls back to JPEG parsing from MJPEG stream
- **Timeout:** 10 seconds per frame
- **Consumers:** TimelapseManager (periodic ~1 minute intervals)

### 1.5. `/stream/overlay/capture` (Parallel Stage 3: Overlayed JPEG Capture)
- **Purpose:** Serve single JPEG frame with text overlays for manual snapshots
- **Implementation:** Inline in Program.cs, reads from /stream/overlay
- **URL:** `http://127.0.0.1:8080/stream/overlay/capture`
- **Format:** Single image/jpeg with overlays (not streaming)
- **Content:** Includes printer state text overlay (temperature, layer, progress, ETA)
- **Timeout:** 10 seconds per frame
- **Consumers:** Manual snapshot requests, debugging

### 2.5. `/stream/mix/capture` (Parallel Stage 5: Mixed Stream JPEG Capture)
- **Purpose:** Serve single JPEG frame from the mixed video+audio stream
- **Implementation:** Inline in Program.cs, reads from /stream/mix
- **URL:** `http://127.0.0.1:8080/stream/mix/capture`
- **Format:** Single image/jpeg (not streaming)
- **Method:** Attempts to parse JPEG from MP4/H.264 stream
- **Timeout:** 10 seconds per frame
- **Consumers:** Future use cases, debugging, frame verification

---

## Data Flow Consumers

### YouTube Broadcast Pipeline
```
/stream/mix → FfmpegStreamer → RTMP → YouTube Live
```

### Direct Web Viewer
```
/stream/overlay → Browser (MJPEG viewer)
```

### Local Recording
```
/stream/mix → File writer → /data/recordings/...
```

### Timelapse Video Creation
```
/stream/source/capture → TimelapseManager → JPEG sequence → ffmpeg → /data/timelapse/...
```

### Manual Overlay Snapshot
```
/stream/overlay/capture → Single JPEG with printer state text
```

### Mixed Stream Frame Capture (Future)
```
/stream/mix/capture → Single JPEG from video+audio mix
```

---

## Configuration

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

### Stream:Source
- **Default:** `http://localhost:8081/webcam/?action=snapshot`
- **Purpose:** Where PrintStreamer fetches raw webcam MJPEG
- **Examples:**
  - Raspberry Pi camera via mjpg-streamer: `http://192.168.1.100:8081/video`
  - Logitech USB camera: `http://192.168.1.100:8081/mjpeg`
  - Axis IP camera: `http://192.168.1.100/axis-cgi/mjpg/video.cgi`

### Overlay options
- **Overlay:StreamSource:** Alternative source for overlay ffmpeg (usually /stream/source)
- **Overlay:Enabled:** Enable/disable overlay text
- **Overlay:TextUpdateInterval:** How often to fetch printer state and update overlay

### Audio options
- **Audio:Enabled:** Enable/disable audio mixing
- **Audio:Folder:** Path to audio files (default: `/data/audio/`)

### Moonraker options
- **Moonraker:BaseUrl:** URL to Moonraker API (for printer state)
- **Moonraker:UpdateInterval:** How often to poll for state changes

---

## Implementation Status

✅ **COMPLETE:**
- Stage 1: /stream/source endpoint (WebCamManager)
- Stage 3: /stream/overlay endpoint (OverlayMjpegStreamer)
- Stage 4: /stream/audio endpoint (AudioBroadcastService)
- Stage 5: /stream/mix endpoint (MixStreamer)
- Stage 0.5: /stream/capture endpoint (JPEG capture for timelapse)
- TimelapseManager updated to use /stream/capture

⏳ **FUTURE:**
- Multi-region streaming (forward to multiple YouTube channels)
- Adaptive bitrate encoding based on network conditions
- Advanced scene detection (pause detection, filament detection)
- Archive optimization (compress and upload recorded broadcasts)

---

## Deployment

### Docker Deployment
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install -y ffmpeg
COPY . /app
WORKDIR /app
EXPOSE 5000
CMD ["dotnet", "printstreamer.dll"]
```

### Local Development
```bash
dotnet build
dotnet run --configuration Debug
# Access at http://localhost:5000
```

### Production
```bash
dotnet build -c Release
dotnet publish -c Release -o ./publish
# Run with: dotnet ./publish/printstreamer.dll
```

