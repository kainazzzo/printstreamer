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
