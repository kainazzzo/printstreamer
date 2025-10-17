# PrintStreamer Architecture

## Overview
PrintStreamer is a .NET 8.0 application that bridges 3D printer MJPEG webcam streams to YouTube Live with automated broadcast management.

## Components

### 1. Program.cs (Main Entry Point)
**Responsibilities:**
- Configuration loading (appsettings.json, env vars, CLI args)
- Mode selection (serve/stream/read)
- Orchestrates all components
- Handles graceful shutdown

**Workflow:**
```
Start
  ↓
Load Config
  ↓
Check Mode
  ├─→ read: MjpegReader (diagnostic mode)
  ├─→ serve: ASP.NET Core server (+ optional YouTube streaming)
  └─→ stream: YouTube streaming only
```

### 2. YouTubeBroadcastService.cs
**Responsibilities:**
- OAuth2 authentication with Google
- Refresh token storage (`youtube_tokens/` directory)
- Live broadcast creation
- Live stream creation and binding
- Broadcast lifecycle management (transition to live, end broadcast)

**Key Methods:**
- `AuthenticateAsync()` - OAuth flow (opens browser on first run)
- `CreateLiveBroadcastAsync()` - Creates broadcast+stream, returns RTMP URL/key
- `TransitionBroadcastToLiveAsync()` - Makes broadcast visible to viewers
- `EndBroadcastAsync()` - Ends the broadcast

**Token Persistence:**
Uses `Google.Apis.Auth.OAuth2.FileDataStore` to save refresh tokens locally. Subsequent runs automatically refresh the access token without user interaction.

### 3. FfmpegStreamer.cs
**Responsibilities:**
- Manages ffmpeg process lifecycle
- Builds ffmpeg command-line arguments
- Handles process output/error streams
- Provides exit notification via `ExitTask`

**ffmpeg Configuration:**
- Codec: H.264 (libx264)
- Preset: veryfast (low latency)
- Bitrate: 2500k with buffering
- Pixel format: yuv420p (YouTube compatible)
- Audio: disabled (no audio stream)
- Output: FLV over RTMP

### 4. MjpegReader.cs
**Responsibilities:**
- Diagnostic tool for MJPEG stream inspection
- Frame extraction from multipart/x-mixed-replace stream
- JPEG marker detection (SOI: 0xFFD8, EOI: 0xFFD9)
- Optional frame saving for debugging

**Use Case:**
Testing/debugging MJPEG sources before streaming to YouTube.

### 5. ASP.NET Core Proxy Server
**Responsibilities:**
- HTTP server on port 8080
- Proxies MJPEG stream to browsers
- Serves test HTML page with embedded stream viewer
- Handles client disconnections gracefully

**Endpoints:**
- `GET /stream` - Proxied MJPEG stream
- `GET /` - Test viewer page

## Data Flow

### Serve Mode (with YouTube streaming)
```
3D Printer Camera
        ↓ (MJPEG/HTTP)
        ↓
  ┌─────┴──────┐
  │            │
  ↓            ↓
Proxy Server  FfmpegStreamer
(port 8080)     ↓ (encode H.264)
  ↓             ↓ (RTMP)
Browser    YouTube Ingest
               ↓
         Live Broadcast
```

### Stream Mode Only
```
3D Printer Camera
        ↓ (MJPEG/HTTP)
  FfmpegStreamer
        ↓ (encode H.264)
        ↓ (RTMP)
  YouTube Ingest
        ↓
  Live Broadcast
```

## Configuration Schema

```json
{
  "Stream": {
    "Source": "string (MJPEG URL or /dev/video0)"
  },
  "YouTube": {
    "Key": "string (manual stream key)",
    "OAuth": {
      "ClientId": "string (Google OAuth client ID)",
      "ClientSecret": "string (Google OAuth client secret)"
    },
    "LiveBroadcast": {
      "Title": "string",
      "Description": "string",
      "Privacy": "public|unlisted|private",
      "CategoryId": "string (YouTube category, default: 28 = Science & Technology)"
    },
    "LiveStream": {
      "Title": "string",
      "Description": "string",
      "IngestionType": "rtmp|rtmps|hls"
    }
  },
  "Mode": "serve|stream|read"
}
```

## Authentication Flow

### First Run (OAuth)
```
App starts
  ↓
AuthenticateAsync()
  ↓
No saved token
  ↓
Open browser → Google OAuth consent screen
  ↓
User grants permission
  ↓
Save refresh token to youtube_tokens/
  ↓
Create YouTubeService with credentials
```

### Subsequent Runs
```
App starts
  ↓
AuthenticateAsync()
  ↓
Load refresh token from youtube_tokens/
  ↓
Exchange for new access token
  ↓
Create YouTubeService with credentials
```

## Broadcast Creation Flow

```
StartYouTubeStreamAsync()
  ↓
AuthenticateAsync()
  ↓
CreateLiveBroadcastAsync()
  ├─→ Create LiveBroadcast resource
  ├─→ Create LiveStream resource
  └─→ Bind stream to broadcast
  ↓
Get RTMP URL + Stream Key
  ↓
Start FfmpegStreamer
  ↓
Wait 10 seconds (ffmpeg connection)
  ↓
TransitionBroadcastToLiveAsync()
  ↓
Stream is LIVE on YouTube
  ↓
Wait for ExitTask (user stops or error)
  ↓
EndBroadcastAsync()
  ↓
Cleanup
```

## Error Handling

### Authentication Failures
- Missing credentials → Print error and exit
- OAuth flow canceled → Print error and exit
- Network errors → Exception caught and logged

### Streaming Failures
- ffmpeg crashes → ExitTask completes, cleanup triggered
- Network interruption → ffmpeg handles reconnection (built-in)
- RTMP connection rejected → ffmpeg logs error, ExitTask completes

### Proxy Server Failures
- Source unreachable → 502 error to client
- Client disconnects → OperationCanceledException caught, logged
- Kestrel errors → ASP.NET Core default handling

## Dependencies

### NuGet Packages
- `Google.Apis.YouTube.v3` - YouTube Data API v3 client
- `Google.Apis.Auth` - OAuth2 authentication
- `Microsoft.AspNetCore.App` - ASP.NET Core framework
- `Newtonsoft.Json` - JSON serialization (transitive)

### External Dependencies
- **ffmpeg** - Must be installed on host system
  - Linux: `apt install ffmpeg` or `dnf install ffmpeg`
  - macOS: `brew install ffmpeg`
  - Windows: Download from ffmpeg.org

## Deployment

### Docker
Multi-stage build:
1. Stage 1: `mcr.microsoft.com/dotnet/sdk:8.0` - Build and publish
2. Stage 2: `mcr.microsoft.com/dotnet/aspnet:8.0` - Runtime

**Configuration via environment:**
```bash
docker run -p 8080:8080 \
  -e Stream__Source="http://printer.local/webcam/?action=stream" \
  -e YouTube__OAuth__ClientId="..." \
  -e YouTube__OAuth__ClientSecret="..." \
  -e Mode=serve \
  printstreamer:latest
```

### Bare Metal
```bash
dotnet publish -c Release
cd bin/Release/net8.0/publish
dotnet printstreamer.dll
```

## Security Considerations

### Credentials Storage
- OAuth tokens stored in `youtube_tokens/` directory
- **Do not commit** `youtube_tokens/` to version control
- **Do not commit** OAuth credentials to `appsettings.json` in public repos
- Use environment variables or Docker secrets in production

### Network Security
- Proxy server listens on `0.0.0.0:8080` (all interfaces)
- Consider firewall rules or reverse proxy with TLS
- MJPEG stream is proxied without authentication
- YouTube RTMP uses TLS (secure by default)

### Token Refresh
- Access tokens expire after ~1 hour
- Refresh tokens valid indefinitely (until revoked)
- Library handles automatic token refresh
- Manual revocation: Google Account → Security → Third-party apps

## Future Enhancements

### Potential Features
- [ ] Audio capture and encoding
- [ ] Frame overlays (timestamp, temperature data from OctoPrint API)
- [ ] Automatic reconnection on stream failure
- [ ] Multiple stream destinations (multi-streaming)
- [ ] Stream health monitoring and alerts
- [ ] Integration with OctoPrint events (start stream on print start)
- [ ] Configurable video quality presets
- [ ] Stream recording to local storage
- [ ] WebSocket API for status monitoring

### Performance Optimizations
- [ ] Adjust ffmpeg encoding preset based on CPU usage
- [ ] Implement stream caching for proxy server
- [ ] Parallel frame processing for overlays
- [ ] Adaptive bitrate based on network conditions
