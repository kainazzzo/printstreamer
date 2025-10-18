# printstreamer

Stream your 3D printer webcam to YouTube Live with automated broadcast creation and MJPEG proxy server.

This app can operate in multiple modes:
- **Serve mode**: Acts as an MJPEG proxy server on port 8080 for local testing
- **Stream mode**: Streams directly to YouTube using ffmpeg
- **Combined mode**: Runs both proxy server AND YouTube streaming simultaneously

The app uses ffmpeg as the streaming engine to keep dependencies minimal and avoid reimplementing video encoding stacks.

## Quick Start

### Prerequisites
1. Install ffmpeg: `sudo apt install ffmpeg` (Debian/Ubuntu) or download from [ffmpeg.org](https://ffmpeg.org)
2. Install .NET 8.0 SDK

### Running Modes

#### 1. Proxy Server Only (for testing)
```bash
dotnet run
# or with explicit mode
dotnet run -- --Mode serve
```
Access the stream at `http://localhost:8080/stream` or view the test page at `http://localhost:8080/`


#### 2. Stream to YouTube (OAuth Only)
```bash
dotnet run -- --Mode stream
```
Requires OAuth credentials set in `appsettings.json` (see setup below). 

The app will:
1. Authenticate with YouTube (browser opens on first run)
2. Create a new live broadcast
3. Create and bind a live stream
4. Start ffmpeg streaming
5. Transition the broadcast to "live" status
6. Print the YouTube watch URL

#### 3. Combined Mode (Proxy + YouTube Streaming)
```bash
dotnet run -- --Mode serve
```
With OAuth configured, this will run the proxy server on port 8080 AND stream to YouTube in the background.

#### 4. Poll Mode (Automated Streaming)
```bash
dotnet run -- --Mode poll
```
Automatically starts/stops YouTube streams based on print jobs (requires Moonraker API configured).

## Configuration

Configuration is loaded from `appsettings.json`, environment variables, or command-line arguments (in that priority order).

### appsettings.json Example
```json
{
  "Stream": {
    "Source": "http://192.168.1.117/webcam/?action=stream&octoAppPortOverride=80&cacheBust=1759967901624",
    "UseNativeStreamer": false
  },
  "YouTube": {
    "OAuth": {
      "ClientId": "YOUR_CLIENT_ID.apps.googleusercontent.com",
      "ClientSecret": "YOUR_CLIENT_SECRET"
    },
    "LiveBroadcast": {
      "Title": "3D Printer Live Stream",
      "Description": "Live from my 3D printer",
      "Privacy": "unlisted",
      "CategoryId": "28"
    },
    "LiveStream": {
      "Title": "Printer Camera Feed",
      "Description": "MJPEG stream from OctoPrint",
      "IngestionType": "rtmp"
    }
  },
  "Moonraker": {
    "ApiUrl": "http://YOUR_MOONRAKER_HOST:7125"
  },
  "Mode": "serve"
}
```

### Environment Variables
Use double-underscores (`__`) for hierarchical keys:
```bash
export Stream__Source="http://printer.local/webcam/?action=stream"
export Stream__UseNativeStreamer=false
export YouTube__OAuth__ClientId="xxx.apps.googleusercontent.com"
export YouTube__OAuth__ClientSecret="GOCSPX-xxx"
export Moonraker__ApiUrl="http://moonraker.local:7125"
export Mode=serve
```

## Streaming Implementations
# PrintStreamer

**PrintStreamer** streams your 3D printer webcam to YouTube Live with automated broadcast creation and MJPEG proxy server. It supports multiple modes, including automated job-based streaming via Moonraker.

---

## Documentation Index

- **[QUICKSTART.md](./QUICKSTART.md)** — Fastest way to get streaming, with runtime configuration examples.
- **[ARCHITECTURE.md](./ARCHITECTURE.md)** — Technical deep-dive, modes, and configuration system.
- **[DOCKER_RELEASE.md](./DOCKER_RELEASE.md)** — Secure Docker build, secrets, and deployment best practices.
- **[PROJECT_SUMMARY.md](./PROJECT_SUMMARY.md)** — Features, security, and development workflow.
- **[NATIVE_STREAMER.md](./NATIVE_STREAMER.md)** — Native .NET streamer details and advanced features.
- **[STREAMER_EXAMPLES.md](./STREAMER_EXAMPLES.md)** — Usage patterns and real-world scenarios.

---

## Key Features

- Automated YouTube Live streaming (OAuth only)
- MJPEG proxy server for local testing
- Poll mode: auto-start/stop streams based on print jobs (Moonraker)
- Secure, production-ready deployment (see Docker guide)

---

## Get Started

1. **Read [QUICKSTART.md](./QUICKSTART.md)** for setup and runtime configuration.
2. **See [DOCKER_RELEASE.md](./DOCKER_RELEASE.md)** for secure deployment and secrets management.
3. **See [ARCHITECTURE.md](./ARCHITECTURE.md)** for technical details and configuration options.

---

## Need Help?

- Troubleshooting: See [QUICKSTART.md](./QUICKSTART.md#quick-troubleshooting)
- Security: See [PROJECT_SUMMARY.md](./PROJECT_SUMMARY.md#security-notes)
- Advanced: See [NATIVE_STREAMER.md](./NATIVE_STREAMER.md) and [STREAMER_EXAMPLES.md](./STREAMER_EXAMPLES.md)

---

**For full details, always refer to the linked markdown documents above.**
5. Save and continue


