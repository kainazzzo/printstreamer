# Native .NET Streamer Implementation

## Overview

The `MjpegToRtmpStreamer` provides a pure .NET implementation for streaming MJPEG video sources to RTMP destinations. Unlike `FfmpegStreamer` which shells out to ffmpeg entirely, this implementation reads and processes the MJPEG stream directly in .NET code.

## Architecture

### Components

#### 1. MjpegToRtmpStreamer
**Purpose**: Main orchestrator for the native streaming pipeline

**Key Features**:
- Connects to MJPEG source via HttpClient
- Validates content-type (multipart/x-mixed-replace)
- Extracts boundary from HTTP headers
- Manages streaming lifecycle
- Uses ArrayPool for memory efficiency

**Flow**:
```
HTTP GET to source
    ↓
Read stream chunks
    ↓
MjpegFrameExtractor
    ↓
Extract JPEG frames
    ↓
RtmpConnection
    ↓
Send to YouTube
```

#### 2. MjpegFrameExtractor
**Purpose**: Parse MJPEG stream and extract individual JPEG frames

**Algorithm**:
- Maintains a rolling buffer of incoming data
- Searches for JPEG SOI marker (0xFF 0xD8)
- Searches for JPEG EOI marker (0xFF 0xD9)
- Extracts complete JPEG between markers
- Removes consumed data from buffer

**Performance**:
- Uses `ReadOnlySpan<byte>` for zero-copy operations
- Pattern matching with `SequenceEqual`
- Efficient buffer management

#### 3. RtmpConnection
**Purpose**: Handle RTMP transmission of JPEG frames

**Current Implementation**:
- Uses ffmpeg as an encoding bridge
- Pipes JPEG frames to ffmpeg stdin
- ffmpeg re-encodes to H.264 and streams RTMP

**Why Hybrid Approach?**:
- .NET controls frame extraction (timing, filtering, overlays)
- ffmpeg handles video encoding (complex, hardware-accelerated)
- Best of both worlds: flexibility + performance

## Comparison: Native vs FFmpeg Streamer

| Feature | FfmpegStreamer | MjpegToRtmpStreamer |
|---------|---------------|---------------------|
| **Implementation** | Shell to ffmpeg | .NET + ffmpeg bridge |
| **Frame Access** | ❌ No | ✅ Yes (in-memory) |
| **Frame Processing** | ❌ No | ✅ Yes (future) |
| **Memory Usage** | Low | Medium |
| **CPU Usage** | Low | Medium |
| **Startup Time** | Fast | Fast |
| **Reconnection** | Manual | Manual |
| **Overlays** | ❌ No | ✅ Possible |
| **Frame Filtering** | ❌ No | ✅ Possible |
| **Complexity** | Low | Medium |

## Configuration

Enable native streamer in `appsettings.json`:

```json
{
  "Stream": {
    "Source": "http://printer.local/webcam/?action=stream",
    "UseNativeStreamer": true
  }
}
```

Or via environment variable:
```bash
export Stream__UseNativeStreamer=true
```

## Use Cases

### When to Use Native Streamer

✅ **Good For**:
- Adding frame overlays (text, graphics, data)
- Frame filtering (skip duplicate frames)
- Frame analysis (quality checks, motion detection)
- Custom frame rate control
- Integration with OctoPrint events
- Debugging MJPEG streams

### When to Use FFmpeg Streamer

✅ **Good For**:
- Simple pass-through streaming
- Minimal CPU/memory usage
- Maximum compatibility
- Production stability

## Performance Characteristics

### Memory Usage
- **Buffer Size**: 64KB rolling buffer (configurable)
- **ArrayPool**: Reuses memory allocations
- **JPEG Storage**: Temporary per-frame allocation
- **Total Overhead**: ~100-200KB per stream

### CPU Usage
- **Frame Extraction**: Minimal (pattern matching)
- **H.264 Encoding**: Same as FFmpeg streamer (done by ffmpeg)
- **Network I/O**: Async, non-blocking

### Latency
- **Frame Detection**: < 1ms per frame
- **Total Latency**: Same as FFmpeg streamer (~2-5 seconds end-to-end)

## Future Enhancements

### Potential Features

#### 1. Frame Overlays
```csharp
// Add text overlay with timestamp and temperature
public async Task<byte[]> AddOverlayAsync(byte[] jpegFrame)
{
    using var image = Image.Load(jpegFrame);
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    var temp = await octoPrintClient.GetExtruderTemperature();
    
    image.Mutate(ctx => {
        ctx.DrawText($"{timestamp} | Temp: {temp}°C", font, Color.White, new PointF(10, 10));
    });
    
    var ms = new MemoryStream();
    image.SaveAsJpeg(ms);
    return ms.ToArray();
}
```

#### 2. Motion Detection
```csharp
// Only send frames when motion detected (save bandwidth)
public bool DetectMotion(byte[] frame1, byte[] frame2)
{
    var diff = CompareFrames(frame1, frame2);
    return diff > threshold;
}
```

#### 3. Adaptive Quality
```csharp
// Reduce quality on slow network
public async Task<byte[]> AdjustQualityAsync(byte[] frame, NetworkStats stats)
{
    var quality = stats.Bandwidth > 5_000_000 ? 95 : 75;
    return ReencodeJpeg(frame, quality);
}
```

#### 4. Frame Rate Control
```csharp
// Dynamically adjust frame rate based on CPU
public bool ShouldSendFrame(int frameNumber, CpuStats cpu)
{
    var targetFps = cpu.Usage > 80 ? 15 : 30;
    return frameNumber % (30 / targetFps) == 0;
}
```

## Image Processing Libraries

For advanced frame manipulation, consider adding:

### SixLabors.ImageSharp
```bash
dotnet add package SixLabors.ImageSharp
```

**Features**:
- Text overlays
- Drawing shapes
- Filters and effects
- Resize/crop
- Format conversion

**Example**:
```csharp
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;

var image = Image.Load(jpegBytes);
image.Mutate(x => x
    .DrawText("Print Progress: 45%", font, Color.White, new PointF(10, 10))
    .DrawRectangle(Color.Red, 2, new Rectangle(50, 50, 100, 100))
);
```

### OpenCvSharp
```bash
dotnet add package OpenCvSharp4
dotnet add package OpenCvSharp4.runtime.ubuntu.20.04-x64
```

**Features**:
- Computer vision algorithms
- Object detection
- Motion tracking
- Advanced filters
- GPU acceleration

## RTMP Implementation Notes

### Current Approach (Hybrid)
The current `RtmpConnection` uses ffmpeg as an encoding bridge:

**Pros**:
- Leverages ffmpeg's mature H.264 encoder
- Hardware acceleration support (NVENC, QSV, etc.)
- Proven reliability
- No need to implement FLV muxing

**Cons**:
- Still depends on external ffmpeg binary
- Limited control over encoding parameters
- Extra process overhead

### Pure .NET RTMP (Future)

For a completely native implementation, consider:

#### Option 1: FFmpeg.AutoGen
```bash
dotnet add package FFmpeg.AutoGen
```
- Direct bindings to FFmpeg libraries
- No process spawning
- Native performance
- Complex API

#### Option 2: SIPSorcery
```bash
dotnet add package SIPSorcery
```
- .NET RTMP implementation
- Built-in FLV muxing
- Simpler API
- Less mature than FFmpeg

#### Option 3: Custom Implementation
Implement RTMP handshake + FLV muxing:
- Full control
- No external dependencies
- Significant development effort
- Maintenance burden

### Recommended Approach

**For Production**: Keep hybrid approach (best balance)
**For Learning**: Try SIPSorcery (pure .NET)
**For Performance**: Use FFmpeg.AutoGen (native libraries)

## Testing

### Unit Tests
```csharp
[Test]
public void MjpegFrameExtractor_ExtractsValidFrames()
{
    var extractor = new MjpegFrameExtractor();
    var testData = CreateMjpegTestData();
    
    extractor.AppendData(testData);
    
    Assert.True(extractor.TryExtractFrame(out var frame));
    Assert.Equal(0xFF, frame.Span[0]); // JPEG SOI
    Assert.Equal(0xD8, frame.Span[1]);
}
```

### Integration Tests
```csharp
[Test]
public async Task MjpegToRtmpStreamer_StreamsToMockRtmp()
{
    var mockSource = new MockMjpegServer();
    var mockRtmp = new MockRtmpServer();
    
    var streamer = new MjpegToRtmpStreamer(
        mockSource.Url, 
        mockRtmp.Url
    );
    
    await streamer.StartAsync();
    
    Assert.True(mockRtmp.ReceivedFrames > 0);
}
```

## Troubleshooting

### Problem: No frames extracted
**Symptoms**: Stream connects but no frames logged
**Causes**:
- Invalid MJPEG format
- Incorrect boundary detection
- Incomplete JPEG markers

**Debug**:
```csharp
// Add logging to frame extractor
Console.WriteLine($"Buffer size: {_buffer.Position}");
Console.WriteLine($"SOI found: {soiIndex >= 0}");
Console.WriteLine($"EOI found: {eoiIndex >= 0}");
```

### Problem: High memory usage
**Symptoms**: Memory grows over time
**Causes**:
- Buffer not clearing after extraction
- Leaked frame data
- No backpressure handling

**Fix**:
```csharp
// Add buffer size limits
if (_buffer.Position > MAX_BUFFER_SIZE)
{
    Console.WriteLine("Buffer overflow, resetting");
    _buffer.SetLength(0);
    _buffer.Position = 0;
}
```

### Problem: Frame lag/delay
**Symptoms**: Frames arrive late
**Causes**:
- Network buffering
- Slow frame processing
- ffmpeg encoding backlog

**Monitor**:
```csharp
var sw = Stopwatch.StartNew();
await rtmpConnection.SendFrameAsync(frame);
if (sw.ElapsedMilliseconds > 100)
{
    Console.WriteLine($"Slow frame send: {sw.ElapsedMilliseconds}ms");
}
```

## Performance Tuning

### Buffer Size
```csharp
// Adjust based on frame size
var bufferSize = frameWidth * frameHeight / 10; // heuristic
var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
```

### Parallel Processing
```csharp
// Process frames in parallel (if order doesn't matter)
var channel = Channel.CreateUnbounded<byte[]>();
var producer = Task.Run(() => ExtractFrames(channel.Writer));
var consumer = Task.Run(() => ProcessFrames(channel.Reader));
```

### Frame Skipping
```csharp
// Skip frames under high load
if (_frameQueue.Count > MAX_QUEUE_SIZE)
{
    Console.WriteLine("Queue full, skipping frame");
    continue;
}
```

## Conclusion

The native streamer provides a solid foundation for advanced video processing while maintaining good performance. The hybrid approach (native frame extraction + ffmpeg encoding) offers the best balance of flexibility and performance for most use cases.

For simple streaming, `FfmpegStreamer` remains the recommended choice.
For advanced features (overlays, filtering, analysis), use `MjpegToRtmpStreamer`.
