# Streamer Examples

## Quick Reference

### Use FFmpeg Streamer
```bash
dotnet run -- --Stream:Source "http://printer.local/webcam/?action=stream"
```
or
```json
{
  "Stream": {
    "Source": "http://printer.local/webcam/?action=stream"
  }
}
```

## Example Scenarios

### Scenario 1: Simple Streaming
**Goal**: Stream 3D printer camera to YouTube 24/7

**Why FFmpeg**:
- Lower CPU usage
- Proven stability
- Hardware acceleration
- Simple setup

**Config**:
```json
{
  "Stream": {
    "Source": "http://printer.local/webcam/?action=stream"
  }
}
```

**Result**: Lowest resource usage, maximum uptime
