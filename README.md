# printstreamer

Simple CLI helper to stream a webcam or MJPEG feed to YouTube using ffmpeg.

This project intentionally uses ffmpeg as the streaming engine and the .NET app launches and monitors the process. That keeps dependencies minimal and avoids reimplementing streaming stacks.

Usage:

1. Install ffmpeg on your machine (package manager / download). On Debian/Ubuntu: `sudo apt install ffmpeg`.
2. Run the project with source and YouTube stream key:

```bash
dotnet run -- --source http://printer.local/webcam/?action=stream --key YOUR_YOUTUBE_STREAM_KEY
```

Notes and next steps:

- Right now audio is disabled. If you need audio, we can add support to capture/encode and mux it into the stream.
- Suggested NuGet packages for future features:
  - CliFx or System.CommandLine — to build a nicer CLI.
  - YoutubeExplode — for interacting with YouTube APIs (e.g., fetch stream status, scheduled streams) though it does not provide RTMP streaming itself.
  - LibVLCSharp / OpenCvSharp — for programmatic frame capture or overlays (if you want to modify frames before streaming).

Security: keep your stream key secret. Consider reading it from a file or environment variable instead of passing on the command line.

If you'd like, I can extend this to: capture audio, add overlays, rotate/rescale the feed, attempt reconnection on failure, or provide a service/daemon wrapper.

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

