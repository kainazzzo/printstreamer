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

[Continue with all sections from original document...]
