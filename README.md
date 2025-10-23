# printstreamer

Stream your 3D printer webcam to YouTube Live with automated broadcast creation and MJPEG proxy server.

This application runs a small web UI and a background poller by default. Configure behavior at runtime using configuration keys (appsettings.json, environment variables, or command-line arguments).


 By default the web UI (proxy) is served on port 8080 and the poller runs as a hosted background service.
 Control YouTube behavior with YouTube:OAuth (OAuth credentials) or YouTube:Key (manual stream key). Use YouTube:StartInServe to optionally start streaming when serving the UI.
The app uses ffmpeg as the streaming engine to keep dependencies minimal and avoid reimplementing video encoding stacks.

## Quick Start

### Prerequisites
1. Install ffmpeg: `sudo apt install ffmpeg` (Debian/Ubuntu) or download from [ffmpeg.org](https://ffmpeg.org)
2. Install .NET 8.0 SDK

### Running the app

The app's runtime behavior is controlled by configuration keys rather than a single "mode" flag. Examples below show common usage patterns.

1) Run the web UI / MJPEG proxy (default)

```bash
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream"
```

Open the control panel at http://localhost:8080 to view the raw MJPEG source and the local HLS preview.

 2) Stream to YouTube (OAuth)

```bash
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream" \
  --YouTube:OAuth:ClientId "YOUR_CLIENT_ID.apps.googleusercontent.com" \
  --YouTube:OAuth:ClientSecret "YOUR_CLIENT_SECRET"
```

On first run, a browser window will open for Google authentication. The app will create and bind a broadcast and start ffmpeg when you promote the encoder to live (or use the Go Live control in the UI).

 3) Automated streaming based on Moonraker jobs (poller)

```bash
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream" \
  --Moonraker:ApiUrl "http://YOUR_MOONRAKER_HOST:7125" \
  --YouTube:OAuth:ClientId "YOUR_CLIENT_ID.apps.googleusercontent.com" \
  --YouTube:OAuth:ClientSecret "YOUR_CLIENT_SECRET"
```

The background poller watches Moonraker print jobs and will start/stop streams automatically when jobs start and finish.

## Configuration

Configuration is loaded from `appsettings.json`, environment variables, or command-line arguments (in that priority order).

### appsettings.json Example
```json
{
  "Stream": {
    "Source": "http://192.168.1.117/webcam/?action=stream&octoAppPortOverride=80&cacheBust=1759967901624"
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
  
}
```

### Environment Variables
Use double-underscores (`__`) for hierarchical keys:
```bash
export Stream__Source="http://printer.local/webcam/?action=stream"
export YouTube__OAuth__ClientId="xxx.apps.googleusercontent.com"
export YouTube__OAuth__ClientSecret="GOCSPX-xxx"
export Moonraker__ApiUrl="http://moonraker.local:7125"
export Stream__Source="http://printer.local/webcam/?action=stream"
```

## Streaming Implementations
# PrintStreamer

**PrintStreamer** streams your 3D printer webcam to YouTube Live with automated broadcast creation and MJPEG proxy server. It includes a web UI and a background poller for Moonraker integration.

---

## Documentation Index

- **[QUICKSTART.md](./QUICKSTART.md)** — Fastest way to get streaming, with runtime configuration examples.
 - **[ARCHITECTURE.md](./ARCHITECTURE.md)** — Technical deep-dive and configuration system.
- **[DOCKER_RELEASE.md](./DOCKER_RELEASE.md)** — Secure Docker build, secrets, and deployment best practices.
- **[PROJECT_SUMMARY.md](./PROJECT_SUMMARY.md)** — Features, security, and development workflow.
- **[STREAMER_EXAMPLES.md](./STREAMER_EXAMPLES.md)** — Usage patterns and real-world scenarios.

---

## Key Features

- Automated YouTube Live streaming (OAuth only)
- MJPEG proxy server for local testing
- Polling: auto-start/stop streams based on print jobs (Moonraker)
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
- Advanced: See [STREAMER_EXAMPLES.md](./STREAMER_EXAMPLES.md)

---

**For full details, always refer to the linked markdown documents above.**
5. Save and continue


