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

#### 2. Stream to YouTube with Manual Stream Key
```bash
dotnet run -- --Mode stream
```
Requires `YouTube:Key` set in `appsettings.json` or via environment variable `YOUTUBE_STREAM_KEY`.

#### 3. Stream to YouTube with Automated Broadcast Creation (OAuth)
Configure OAuth credentials in `appsettings.json` (see setup below), then:
```bash
dotnet run -- --Mode stream
```
The app will:
1. Authenticate with YouTube (browser opens on first run)
2. Create a new live broadcast
3. Create and bind a live stream
4. Start ffmpeg streaming
5. Transition the broadcast to "live" status
6. Print the YouTube watch URL

#### 4. Combined Mode (Proxy + YouTube Streaming)
```bash
dotnet run -- --Mode serve
```
With OAuth or stream key configured, this will run the proxy server on port 8080 AND stream to YouTube in the background.

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
    "Key": "",
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
  "Mode": "serve"
}
```

### Environment Variables
Use double-underscores (`__`) for hierarchical keys:
```bash
export Stream__Source="http://printer.local/webcam/?action=stream"
export Stream__UseNativeStreamer=false
export YouTube__Key="your-stream-key"
export Mode=stream
```

## Streaming Implementations

PrintStreamer supports two streaming implementations:

### 1. FFmpeg Streamer (Default)
- **Recommended for**: Production use, stability, minimal resource usage
- **How it works**: Shells out to ffmpeg process entirely
- **Pros**: Battle-tested, hardware acceleration support, low overhead
- **Cons**: Limited frame-level control, no in-app processing

### 2. Native .NET Streamer
- **Recommended for**: Advanced features, frame manipulation, debugging
- **How it works**: .NET reads MJPEG stream, extracts frames, pipes to ffmpeg for encoding
- **Pros**: Frame access, overlay support, custom filtering, motion detection
- **Cons**: Slightly higher CPU/memory usage

**Enable Native Streamer**:
```json
{
  "Stream": {
    "UseNativeStreamer": true
  }
}
```

See [NATIVE_STREAMER.md](NATIVE_STREAMER.md) for detailed documentation.

## YouTube API Setup

To use automated broadcast creation (recommended for production use), you need OAuth2 credentials from Google Cloud Console:

### Step 1: Create Google Cloud Project
1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Create a new project or select an existing one

### Step 2: Enable YouTube Data API v3
1. Navigate to "APIs & Services" → "Library"
2. Search for "YouTube Data API v3" and enable it

### Step 3: Configure OAuth Consent Screen
1. Go to "APIs & Services" → "OAuth consent screen"
2. Choose "External" user type (unless you have a Google Workspace)
3. Fill in required fields (app name, user support email, developer contact)
4. Add test users (your YouTube account email)
5. Save and continue

### Step 4: Create OAuth Credentials
1. Go to "APIs & Services" → "Credentials"
2. Click "Create Credentials" → "OAuth client ID"
3. Choose "Desktop app" as application type
4. Download the JSON file containing `client_id` and `client_secret`

### Step 5: Add to Configuration
Copy the values to `appsettings.json`:
```json
"YouTube": {
  "OAuth": {
    "ClientId": "1234567890-abcdefghijk.apps.googleusercontent.com",
    "ClientSecret": "GOCSPX-yourClientSecretHere"
  }
}
```

### First Run OAuth Flow
On first run, the app will:
1. Open your default browser
2. Prompt you to log in to Google
3. Ask for permission to manage your YouTube account
4. Save a refresh token to `youtube_tokens/` directory
5. Subsequent runs will use the saved token automatically

**Note**: If you're only using ffmpeg to push RTMP to an existing stream (no automated broadcast creation), you only need the stream key from YouTube Studio → "Go live" → "Stream key". Set it via `YouTube:Key` in config or the `YOUTUBE_STREAM_KEY` environment variable.


Docker
------
You can build a container image for the app. The image does NOT include an `appsettings.json` file by design — pass configuration via environment variables at runtime. Example:

Build the image:

```bash
docker build -t printstreamer:latest .
```

Run the container and pass your OctoPrint stream URL (note the double-underscore `__` to set hierarchical config keys):

```bash
docker run --rm -p 8080:8080 \
  -e Stream__Source="http://192.168.1.117/webcam/?action=stream&octoAppPortOverride=80&cacheBust=1759967901624" \
  -e Mode=serve \
  printstreamer:latest
```

If you want to pass the YouTube key as an environment variable instead of CLI, use `-e YouTube__Key=yourkey` or set `YOUTUBE_STREAM_KEY`.

