dotnet printstreamer.dll

# PrintStreamer Architecture

## Overview
PrintStreamer is a .NET 8.0 application for streaming 3D printer MJPEG webcam feeds to YouTube Live, with automated broadcast management and flexible deployment.

---

## Core Components

### Program.cs (Main Entry Point)
- Loads configuration (CLI args, env vars, appsettings.json)
- Starts the web UI (Serve) and the background poller as configured
- Orchestrates all services and handles graceful shutdown

### YouTubeControlService.cs
- Handles OAuth2 authentication and token storage
- Manages YouTube broadcast and stream lifecycle (create, start, end)
- Persists refresh tokens in `youtube_tokens/` for seamless re-authentication

### FfmpegStreamer.cs
- Manages ffmpeg process for video encoding and streaming
- Configures H.264, bitrate, and RTMP output for YouTube
- Monitors process output and handles exit/cleanup

### MjpegReader.cs
- Diagnostic tool for MJPEG stream inspection and frame extraction
- Used for debugging MJPEG sources before streaming

### Web UI
- Serves the web UI and webcam MJPEG feed on port 8080
- Configure the camera input using `Stream:Source` (direct camera URL). Use an external relay only when relaying is required.
- Handles client connections and the test viewer page

---

## Data Flow

### Serve (Web UI + YouTube)
```
3D Printer Camera (MJPEG)
        ↓
Direct camera URL or optional external relay (configured via Stream:Source)
        ↓
   FfmpegStreamer
        ↓
   YouTube Live
```

### Stream
```
3D Printer Camera (MJPEG)
        ↓
   FfmpegStreamer
        ↓
   YouTube Live
```

### Poller (Moonraker-driven automated streaming)
```
Moonraker API (job status)
        ↓
   Program.cs (poll loop)
        ↓
   Start/stop YouTube stream based on print jobs
```

---

## Configuration

PrintStreamer supports configuration via command-line switches, environment variables, or `appsettings.json`. See [QUICKSTART.md](./QUICKSTART.md) and [DOCKER_RELEASE.md](./DOCKER_RELEASE.md) for usage patterns.

---

## Authentication Flow

On first run, the app opens a browser for Google OAuth. The refresh token is stored in `youtube_tokens/` for future runs. All YouTube API access is via OAuth2.

---

## Dependencies

- .NET 8.0 SDK
- ffmpeg (external process)
- Google.Apis.YouTube.v3, Google.Apis.Auth (NuGet)
- Microsoft.AspNetCore.App (NuGet)

---

## Deployment

See [DOCKER_RELEASE.md](./DOCKER_RELEASE.md) for Docker and secrets management. Multi-stage Docker builds are supported. For bare metal, use `dotnet publish` and run the output.

---

## Security

- OAuth tokens are stored locally and should not be committed to version control
- Use Docker secrets or a secrets manager for sensitive values in production
- The web UI serves the webcam MJPEG feed. If you deploy an external relay, ensure it listens on the intended interfaces and protect it with a firewall or reverse proxy as needed

---

## Future Directions

- Audio capture and encoding
- Frame overlays (e.g., print status, temperature)
- Multi-streaming and stream health monitoring
- Integration with printer events (e.g., start/stop on print job)
- Performance optimizations and advanced diagnostics
