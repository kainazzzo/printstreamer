# Quick Start Guide

> Note: PrintStreamer has shifted its focus from "streaming-first" to being a reliable source provider for OBS and other ingest tools. If you use OBS, consider using the `/stream/obs-urlsource/overlay` endpoint as a source for overlay text and structured values — see [OBS URLSource Overlay docs](../../OBS_URLSOURCE_OVERLAY.md). Streaming (YouTube) remains supported but PrintStreamer now prioritizes feeding sources to OBS and similar tools.

## 🚀 Get Streaming in 5 Minutes

### Step 1: Prerequisites (1 minute)
```bash
# Verify .NET 8.0
dotnet --version  # Should show 8.x.x

# Install ffmpeg
sudo apt install ffmpeg  # Ubuntu/Debian
# or: brew install ffmpeg  # macOS

# Verify ffmpeg
ffmpeg -version
```

### Step 2: Configure Source (1 minute)
Set your printer stream source at runtime using command-line switches:
```bash
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream"
```

### Step 3: Test Locally (1 minute)
```bash
# Start the app
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream"

# Open in browser
open http://localhost:8080
```

You should see your printer's camera feed! 🎉

### Step 4: Setup YouTube (OAuth Only)

PrintStreamer uses OAuth for YouTube streaming. You can also provide a manual stream key via YouTube:Key for non-OAuth scenarios.

Set your OAuth credentials at runtime using command-line switches:
```bash
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream" \
  --YouTube:OAuth:ClientId "YOUR_CLIENT_ID.apps.googleusercontent.com" \
  --YouTube:OAuth:ClientSecret "YOUR_CLIENT_SECRET"
```
**Warning:** Never pass your client secret on the command line in production! Use Docker secrets or a secrets manager for secure deployments. See `DOCKER_RELEASE.md` for secure setup.

On first run, a browser window will open for Google authentication. Approve access to your YouTube account.

### Step 5: Start Streaming! (30 seconds)
```bash
# Stream to YouTube
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream" \
  --YouTube:OAuth:ClientId "YOUR_CLIENT_ID.apps.googleusercontent.com" \
  --YouTube:OAuth:ClientSecret "YOUR_CLIENT_SECRET"

# Or run web UI + streaming together (startYouTubeInServe via config)
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream" \
  --YouTube:OAuth:ClientId "YOUR_CLIENT_ID.apps.googleusercontent.com" \
  --YouTube:OAuth:ClientSecret "YOUR_CLIENT_SECRET"

# Or use the background poller for automated streaming (requires Moonraker API)
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream" \
  --Moonraker:ApiUrl "http://YOUR_MOONRAKER_HOST:7125" \
  --YouTube:OAuth:ClientId "YOUR_CLIENT_ID.apps.googleusercontent.com" \
  --YouTube:OAuth:ClientSecret "YOUR_CLIENT_SECRET"
```

---

## 🎯 Common Use Cases

### Use Case 1: Just Testing?
```bash
# Run web UI only (no YouTube) — web UI serves the webcam MJPEG feed; configure camera input with `Stream:Source` (direct camera URL). Use an external relay only when relaying is required.
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream"
# View at http://localhost:8080
```

### Use Case 2: Stream to YouTube (OAuth)
```bash
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream" \
  --YouTube:OAuth:ClientId "YOUR_CLIENT_ID.apps.googleusercontent.com" \
  --YouTube:OAuth:ClientSecret "YOUR_CLIENT_SECRET"
```
On first run, authorize in your browser. The app will print the YouTube URL.

### Use Case 3: Automated streaming (Poller)
```bash
dotnet run -- --Stream:Source "http://YOUR_PRINTER_IP/webcam/?action=stream" \
  --Moonraker:ApiUrl "http://YOUR_MOONRAKER_HOST:7125" \
  --YouTube:OAuth:ClientId "YOUR_CLIENT_ID.apps.googleusercontent.com" \
  --YouTube:OAuth:ClientSecret "YOUR_CLIENT_SECRET"
```
The app will automatically start/stop YouTube streams based on print jobs.

### Use Case 4: Run in Docker?

Before building or running the container, edit `appsettings.Home.json` in the repository root and set your own values (for example: `Stream:Source`, `YouTube:OAuth:ClientId`, `YouTube:OAuth:ClientSecret`, `Moonraker:ApiUrl`, and any other environment-specific settings). The Docker helper expects `appsettings.Home.json` to exist and mounts it read-only into the container.

You can build & run manually:

```bash
# Build image
docker build -t printstreamer .

# Run (example, not secure for secrets)
docker run -p 8080:8080 \
  --name printstreamer \
  -e "ASPNETCORE_ENVIRONMENT=Home" \
  -v "$(pwd)/appsettings.Home.json:/app/appsettings.Home.json:ro" \
  -v "$HOME/.printstreamer:/app/data" \
  printstreamer
```

Or use the provided helper which publishes the project, builds the image and starts the container for you:

```bash
# Make executable if needed, then run
./scripts/docker.sh

# Force interactive mode (useful for first-time OAuth)
INTERACTIVE=1 ./scripts/docker.sh

# Override defaults (example)
HOST_PORT=9090 DOTNET_IMAGE=myregistry/printstreamer:latest ./scripts/docker.sh
```

Notes about the helper script (`scripts/docker.sh`):
- It requires `docker` on your PATH and will fail if `appsettings.Home.json` is missing.
- It automatically mounts `~/.printstreamer` into the container so tokens and state persist across runs.
- It prints a fully-quoted `docker run` command before executing so you can inspect or copy it.
- To pass a one-time OAuth code for non-interactive auth, set `YOUTUBE_OAUTH_CODE` in the environment.

**Warning:** Never store or pass client secrets insecurely in production. Use Docker secrets, a secrets manager, or the secure guidelines in `DOCKER_RELEASE.md`.

#### Interactive OAuth inside Docker
For the initial YouTube OAuth flow, the container may need interactive input. The helper script supports this:

- Run interactively (allocates a TTY) so the app can display an auth URL and accept a pasted code:

```bash
INTERACTIVE=1 ./scripts/docker.sh
```

- The helper ensures `~/.printstreamer/tokens` exists and will persist the OAuth token there (the script creates `~/.printstreamer/youtube_reuse_store.json` and the `tokens` directory when needed). After the first interactive authorization the token is saved to your host `~/.printstreamer` so future runs can be headless.

---

## 🆘 Quick Troubleshooting

### Timelapse / Slicer integration

If you plan to use the Timelapse features that map frames to print layers, it's recommended to emit print-stats information from your slicer so the printer/Moonraker exposes current layer and total layers via the `print_stats` object. Add these G-code lines in your slicer:

- In "machine start" (at the beginning of the print):

```
SET_PRINT_STATS_INFO TOTAL_LAYER=[total_layer_count]
```

- In "before layer change" (or a layer change script):

```
SET_PRINT_STATS_INFO CURRENT_LAYER={layer_num + 1}
```

These lines let the printer update `print_stats` (accessible at `/printer/objects/query?print_stats`) with the current and total layer counts so PrintStreamer can track progress without downloading G-code files.

**Important:** By default, timelapse capture is gated until layer 1 begins (skipping leveling/homing at layer 0). To disable this gating and capture from the very start of the print, set:
- `Timelapse:StartAfterLayer1=false` - disable gating (capture from print start)

### Filament details in YouTube descriptions
To include filament brand/type/color and used/total length in your live/timelapse descriptions, configure your slicer (OrcaSlicer/PrusaSlicer) to emit additional `SET_PRINT_STATS_INFO` lines so Moonraker exposes them under `print_stats.info`. Add the following to your slicer:

- In "machine start" G-code (start of print):

```
SET_PRINT_STATS_INFO TOTAL_LAYER=[total_layer_count] \
  FILAMENT_TYPE=[filament_type] \
  FILAMENT_BRAND=[filament_vendor] \
  FILAMENT_COLOR=[filament_colour] \
  FILAMENT_TOTAL_MM=[filament_total]
```

- In "before layer change" (or layer change script):

```
SET_PRINT_STATS_INFO CURRENT_LAYER={layer_num + 1} FILAMENT_USED_MM=[filament_used]
```

Notes:
- Variable names come from OrcaSlicer/PrusaSlicer. If your profile uses `filament_brand` instead of `filament_vendor`, substitute accordingly.
- For multi-material prints, use indexed variables (e.g., `[filament_type[0]]`) or pick the active tool’s index.
- PrintStreamer reads both lowercase and uppercase keys (e.g., `filament_color` or `FILAMENT_COLOR`). The examples above match what the app expects.
- If you don’t emit these lines, Moonraker’s `print_stats.info` won’t contain filament details and the description will omit them.


### Problem: "Error: Stream:Source is required"
**Fix**: Pass the source as a runtime switch:
```bash
dotnet run -- --Stream:Source "http://printer.local/webcam/?action=stream"
```

### Problem: "ffmpeg: command not found"
**Fix**: Install ffmpeg
```bash
sudo apt install ffmpeg  # Linux
brew install ffmpeg      # macOS
```

### Problem: "Port 8080 already in use"
**Fix**: Kill existing process
```bash
sudo lsof -i :8080
sudo kill -9 <PID>
```

### Problem: Stream is laggy/stuttering
**Fix**: Reduce bitrate in `FfmpegStreamer.cs` line 111:
```csharp
-b:v 2500k  // Change to -b:v 1500k
```

### Problem: Can't see video on YouTube
**Fix**: 
1. Check YouTube Studio for errors
2. Verify stream key is correct
3. Wait 10-30 seconds for YouTube to process
4. Check ffmpeg output for encoding errors

---

## 📖 Next Steps

### Want to Learn More?
- Read [ARCHITECTURE.md](ARCHITECTURE.md) for technical details
- See [STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md) for advanced usage


### Want to Customize?
- Change video quality: Edit `FfmpegStreamer.cs` encoding args

- Change port: Edit `Program.cs` line 82: `options.ListenAnyIP(8080)`

### Want to Deploy?
- Use Docker: See [README.md](README.md#docker)
- Run as systemd service: Create `/etc/systemd/system/printstreamer.service`
- Use docker-compose: Create `docker-compose.yml` with restart policy

---

## ✅ Verification Checklist

After setup, verify:
- [ ] `dotnet run` starts without errors
- [ ] http://localhost:8080 shows camera feed
- [ ] ffmpeg process is running (`ps aux | grep ffmpeg`)
- [ ] YouTube stream is live (check YouTube Studio)
- [ ] Stream latency is acceptable (< 10 seconds)
- [ ] No frame drops (check ffmpeg output)

---

## 🎓 Learning Resources

### Understanding the Code
```
Program.cs          → Start here, main flow
FfmpegStreamer.cs   → How ffmpeg is called
YouTubeControlService.cs → YouTube integration
```

### Key Concepts
- **MJPEG**: Motion JPEG, series of JPEG images
- **RTMP**: Real-Time Messaging Protocol for streaming
- **H.264**: Video compression codec
- **OAuth2**: Authorization protocol for YouTube API
- **ASP.NET Core**: Web framework for the web UI

---

**Ready to stream?** Run `dotnet run` and visit http://localhost:8080! 🎬
