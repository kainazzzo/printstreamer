# PrintStreamer Documentation Index

Welcome to PrintStreamer - your complete solution for streaming 3D printer webcams to YouTube Live!

## 📚 Documentation Structure

### Getting Started
1. **[QUICKSTART.md](QUICKSTART.md)** - Get up and running in 5 minutes
   - Installation
   - Basic configuration
   - First stream
   - Common troubleshooting

2. **[README.md](README.md)** - Complete user guide
   - All features overview
   - Detailed setup instructions
   - YouTube OAuth setup
   - Docker deployment
   - Configuration reference

### Understanding the System
3. **[ARCHITECTURE.md](ARCHITECTURE.md)** - Technical deep-dive
   - Component overview
   - Data flow diagrams
   - Class structure
   - Configuration schema
   - Error handling
   - Security considerations

4. **[PROJECT_SUMMARY.md](PROJECT_SUMMARY.md)** - High-level overview
   - What we built
   - Technologies used
   - Key features
   - Development workflow
   - Future roadmap

### Advanced Topics
5. **[NATIVE_STREAMER.md](NATIVE_STREAMER.md)** - Native .NET streamer guide
   - Implementation details
   - Frame extraction algorithm
   - Performance characteristics
   - Use cases
   - Future enhancements
   - Image processing libraries

6. **[STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md)** - Usage examples
   - FFmpeg vs Native comparison
   - Real-world scenarios
   - Code examples
   - Performance benchmarks
   - Decision matrix

## 🎯 Quick Navigation

### I Want To...

#### Stream Video
→ Start with [QUICKSTART.md](QUICKSTART.md)

#### Understand How It Works
→ Read [ARCHITECTURE.md](ARCHITECTURE.md)

#### Add Custom Features
→ Check [NATIVE_STREAMER.md](NATIVE_STREAMER.md)

#### See Examples
→ Browse [STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md)

#### Deploy to Production
→ Follow [README.md#docker](README.md#docker)

#### Troubleshoot Issues
→ See [QUICKSTART.md#troubleshooting](QUICKSTART.md#troubleshooting)

## 🗂️ File Reference

### Source Code
| File | Purpose |
|------|---------|
| `Program.cs` | Main entry point, orchestration |
| `YouTubeService.cs` | OAuth2 + YouTube API integration |
| `FfmpegStreamer.cs` | FFmpeg-based streamer (default) |
| `MjpegToRtmpStreamer.cs` | Native .NET streamer (advanced) |
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
| `scripts/run_printstreamer.sh` | Development helper |

## 📖 Reading Guide by Role

### For End Users
1. [QUICKSTART.md](QUICKSTART.md) - Setup and run
2. [README.md](README.md) - Configuration options
3. [STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md) - Usage patterns

### For Developers
1. [ARCHITECTURE.md](ARCHITECTURE.md) - System design
2. [NATIVE_STREAMER.md](NATIVE_STREAMER.md) - Implementation details
3. [PROJECT_SUMMARY.md](PROJECT_SUMMARY.md) - Development workflow

### For DevOps
1. [README.md#docker](README.md#docker) - Container deployment
2. [ARCHITECTURE.md#security](ARCHITECTURE.md#security-considerations) - Security setup
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
- Comparison: [STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md)
- FFmpeg Details: [ARCHITECTURE.md#ffmpegstreamer](ARCHITECTURE.md#3-ffmpegstreamercs)
- Native Details: [NATIVE_STREAMER.md](NATIVE_STREAMER.md)

### Docker
- Quick Start: [QUICKSTART.md#use-case-4](QUICKSTART.md#use-case-4-run-in-docker)
- Full Guide: [README.md#docker](README.md#docker)
- Build Process: [ARCHITECTURE.md#docker](ARCHITECTURE.md#docker)

### Troubleshooting
- Common Issues: [QUICKSTART.md#troubleshooting](QUICKSTART.md#troubleshooting)
- Detailed: [PROJECT_SUMMARY.md#troubleshooting](PROJECT_SUMMARY.md#troubleshooting)
- Native Streamer: [NATIVE_STREAMER.md#troubleshooting](NATIVE_STREAMER.md#troubleshooting)

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
NATIVE_STREAMER.md (Architecture section)
    ↓
STREAMER_EXAMPLES.md (All scenarios)
    ↓
Experiment with native streamer
```

### Advanced Path
```
ARCHITECTURE.md (Complete)
    ↓
NATIVE_STREAMER.md (Complete)
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
dotnet run -- --Mode stream

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
Stream:UseNativeStreamer   # true/false
YouTube:Key                # Manual stream key
YouTube:OAuth:ClientId     # OAuth client ID
YouTube:OAuth:ClientSecret # OAuth client secret
Mode                       # serve/stream/read
```

### Important URLs
```
http://localhost:8080/        # Test viewer
http://localhost:8080/stream  # MJPEG proxy
https://studio.youtube.com    # YouTube Studio
https://console.cloud.google.com  # Google Cloud Console
```

## 📊 Feature Matrix

| Feature | FFmpeg Streamer | Native Streamer | Docs |
|---------|----------------|-----------------|------|
| Basic Streaming | ✅ | ✅ | [README.md](README.md) |
| Low Resource | ✅ | ⚠️ | [STREAMER_EXAMPLES.md](STREAMER_EXAMPLES.md) |
| Frame Access | ❌ | ✅ | [NATIVE_STREAMER.md](NATIVE_STREAMER.md) |
| Overlays | ❌ | 🔜 | [NATIVE_STREAMER.md#frame-overlays](NATIVE_STREAMER.md#1-frame-overlays) |
| Motion Detection | ❌ | 🔜 | [NATIVE_STREAMER.md#motion-detection](NATIVE_STREAMER.md#2-motion-detection) |

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
