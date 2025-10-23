# PrintStreamer Documentation Index

Welcome to PrintStreamer - your complete solution for streaming 3D printer webcams to YouTube Live!

## 📚 Documentation Structure


### Getting Started
1. **[QUICKSTART.md](QUICKSTART.md)** — 5-minute setup, runtime configuration, troubleshooting
2. **[README.md](README.md)** — High-level guide, navigation, and links to all docs
3. **[DOCKER_RELEASE.md](DOCKER_RELEASE.md)** — Secure Docker build, secrets, and deployment best practices


### Understanding the System
4. **[ARCHITECTURE.md](ARCHITECTURE.md)** — Technical deep-dive, data flow, configuration, security
5. **[PROJECT_SUMMARY.md](PROJECT_SUMMARY.md)** — Features, technologies, workflow, roadmap


### Advanced Topics
7. **[STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md)** — Usage patterns, real-world scenarios, code examples
### Full Documentation List

- [README.md](README.md)
- [QUICKSTART.md](QUICKSTART.md)
- [DOCKER_RELEASE.md](DOCKER_RELEASE.md)
- [ARCHITECTURE.md](ARCHITECTURE.md)
- [PROJECT_SUMMARY.md](PROJECT_SUMMARY.md)
- [STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md)

## 🎯 Quick Navigation

### I Want To...

#### Stream Video
→ Start with [QUICKSTART.md](QUICKSTART.md)

#### Understand How It Works
→ Read [ARCHITECTURE.md](ARCHITECTURE.md)

#### See Examples
→ Browse [STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md)

#### Deploy to Production
→ See [DOCKER_RELEASE.md](DOCKER_RELEASE.md) for secure Docker and deployment

#### Troubleshoot Issues
→ See [QUICKSTART.md#troubleshooting](QUICKSTART.md#troubleshooting)

## 🗂️ File Reference

### Source Code
| File | Purpose |
|------|---------|
| `Program.cs` | Main entry point, orchestration |
| `YouTubeService.cs` | OAuth2 + YouTube API integration |
| `FfmpegStreamer.cs` | FFmpeg-based streamer |
| `IStreamer.cs` | Common streamer interface |

### Configuration
| File | Purpose |
|------|---------|
| `appsettings.json` | Main configuration file |
| `Dockerfile` | Container build instructions |
| `printstreamer.csproj` | .NET project configuration |

### Scripts
| File | Purpose |
|------|---------|
| `scripts/package.sh` | Build and package the app |
| `scripts/release.sh` | Build and release artifact |

## 📖 Reading Guide by Role

### For End Users
1. [QUICKSTART.md](QUICKSTART.md) - Setup and run
2. [README.md](README.md) - Configuration options
3. [STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md) - Usage patterns

### For Developers
1. [ARCHITECTURE.md](ARCHITECTURE.md) - System design
2. [PROJECT_SUMMARY.md](PROJECT_SUMMARY.md) - Development workflow
3. [DOCKER_RELEASE.md](DOCKER_RELEASE.md) - Docker and secrets

### For DevOps
1. [DOCKER_RELEASE.md](DOCKER_RELEASE.md) - Secure Docker deployment
2. [ARCHITECTURE.md#security](ARCHITECTURE.md#security) - Security setup
3. [PROJECT_SUMMARY.md#troubleshooting](PROJECT_SUMMARY.md#troubleshooting) - Common issues

## 🔍 By Topic

### Configuration
- Basic: [QUICKSTART.md#step-2](QUICKSTART.md#step-2-configure-source-1-minute)
- Complete: [README.md#configuration](README.md#configuration)
- Environment: [PROJECT_SUMMARY.md#environment-variables](PROJECT_SUMMARY.md#environment-variables)

### YouTube Setup
- Quick: [QUICKSTART.md#step-4](QUICKSTART.md#step-4-setup-youtube-2-minutes)
- Detailed: [README.md#youtube-api-setup](README.md#youtube-api-setup)
- OAuth Flow: [ARCHITECTURE.md#authentication-flow](ARCHITECTURE.md#authentication-flow)

### Streaming Options
- Examples: [STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md)
- FFmpeg Details: [ARCHITECTURE.md#ffmpegstreamer](ARCHITECTURE.md#3-ffmpegstreamercs)

### Docker
- Quick Start: [QUICKSTART.md#use-case-4](QUICKSTART.md#use-case-4-run-in-docker)
- Full Guide: [DOCKER_RELEASE.md](DOCKER_RELEASE.md)
- Build Process: [ARCHITECTURE.md#deployment](ARCHITECTURE.md#deployment)

### Troubleshooting
- Common Issues: [QUICKSTART.md#troubleshooting](QUICKSTART.md#troubleshooting)
- Detailed: [PROJECT_SUMMARY.md#troubleshooting](PROJECT_SUMMARY.md#troubleshooting)

## 🎓 Learning Path

### Beginner Path
```
QUICKSTART.md
    ↓
README.md (Configuration section)
    ↓
STREAMER_EXAMPLES.md (FFmpeg examples)
    ↓
Try it yourself!
```

### Intermediate Path
```
ARCHITECTURE.md (Overview + Components)
    ↓
STREAMER_EXAMPLES.md (All scenarios)
    ↓
Explore configuration options
```

### Advanced Path
```
ARCHITECTURE.md (Complete)
    ↓
Source code exploration
    ↓
Add custom features
```

## 🔧 Quick Reference

### Common Commands
```bash
# Run proxy server
dotnet run

# Stream to YouTube
dotnet run -- --Stream:Source "http://printer.local/webcam/?action=stream" --YouTube:OAuth:ClientId "YOUR_CLIENT_ID" --YouTube:OAuth:ClientSecret "YOUR_CLIENT_SECRET"

# Build Docker image
docker build -t printstreamer .

# Run in Docker
docker run -p 8080:8080 \
  -e Stream__Source="http://printer/webcam/?action=stream" \
  printstreamer
```

### Configuration Keys
```
Stream:Source              # MJPEG URL
YouTube:Key                # Manual stream key
YouTube:OAuth:ClientId     # OAuth client ID
YouTube:OAuth:ClientSecret # OAuth client secret
Serve:Enabled              # true/false (serve the web UI)
```

### Important URLs
```
http://localhost:8080/        # Test viewer
http://localhost:8080/stream  # MJPEG proxy
https://studio.youtube.com    # YouTube Studio
https://console.cloud.google.com  # Google Cloud Console
```

## 📊 Feature Matrix

| Feature | FFmpeg Streamer | Docs |
|---------|----------------|------|
| Basic Streaming | ✅ | [README.md](README.md) |
| Low Resource | ✅ | [STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md) |
| Hardware Acceleration | ✅ | [ARCHITECTURE.md](ARCHITECTURE.md) |

Legend: ✅ Supported | ⚠️ With caveats | ❌ Not supported | 🔜 Planned

## 🆘 Getting Help

### Documentation Not Helping?
1. Check [QUICKSTART.md#troubleshooting](QUICKSTART.md#troubleshooting) first
2. Review [PROJECT_SUMMARY.md#common-issues](PROJECT_SUMMARY.md#common-issues)
3. Enable verbose logging (add to Program.cs)
4. Check ffmpeg output for encoding errors

### Want to Contribute?
1. Read [PROJECT_SUMMARY.md#contributing](PROJECT_SUMMARY.md#contributing)
2. Understand [ARCHITECTURE.md](ARCHITECTURE.md)
3. Follow existing code patterns
4. Add tests and documentation

## 📝 Document Changelog

### Version 1.0 (Current)
- Complete documentation suite
- FFmpeg streamer implementation
- Native .NET streamer implementation
- YouTube OAuth integration
- Docker support
- Comprehensive examples

### Planned for 1.1
- Frame overlay examples with ImageSharp
- OctoPrint integration guide
- Advanced configuration patterns
- Performance tuning guide
- Production deployment checklist

---

**Start here**: [QUICKSTART.md](QUICKSTART.md)

**Questions?** Check the relevant document above or explore the source code!
