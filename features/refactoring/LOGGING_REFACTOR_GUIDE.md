# Logging Refactoring Guide

This document describes the pattern for replacing `Console.WriteLine` with proper ILogger throughout the codebase.

## Pattern for Services

### Before:
```csharp
internal class MyService
{
    private readonly IConfiguration _config;
    
    public MyService(IConfiguration config)
    {
        _config = config;
    }
    
    public void DoSomething()
    {
        Console.WriteLine($"[MyService] Processing item: {itemId}");
        Console.WriteLine($"[MyService] Error: {ex.Message}");
    }
}
```

### After:
```csharp
internal class MyService
{
    private readonly IConfiguration _config;
    private readonly ILogger<MyService> _logger;
    
    public MyService(IConfiguration config, ILogger<MyService> logger)
    {
        _config = config;
        _logger = logger;
    }
    
    public void DoSomething()
    {
        _logger.LogInformation("Processing item: {ItemId}", itemId);
        _logger.LogError(ex, "Error occurred");
    }
}
```

## Log Level Mappings

| Console Pattern | ILogger Equivalent |
|----------------|-------------------|
| `Console.WriteLine("[Info] ...")` | `_logger.LogInformation(...)` |
| `Console.WriteLine("[Warn] ...")` or `Warning:` | `_logger.LogWarning(...)` |
| `Console.WriteLine("[Error] ...")` or error messages | `_logger.LogError(...)` |
| `Console.WriteLine("[Debug] ...")` or verbose proxy logs | `_logger.LogDebug(...)` |
| Exception messages with `ex.Message` | `_logger.LogError(ex, "message")` - includes full exception |

## Structured Logging

Use named parameters instead of string interpolation:

### Bad:
```csharp
_logger.LogInformation($"Processing file {filename} at {DateTime.Now}");
```

### Good:
```csharp
_logger.LogInformation("Processing file {Filename} at {Timestamp}", filename, DateTime.Now);
```

## Services to Update

### Completed:
- [x] YouTubeControlService.cs - Constructor updated with ILogger

### In Progress:
- [ ] Complete all Console.WriteLine replacements in YouTubeControlService.cs

### Remaining:
- [ ] MoonrakerPoller.cs
- [ ] MoonrakerPollerService.cs  
- [ ] StreamService.cs
- [ ] StreamOrchestrator.cs
- [ ] AudioBroadcastService.cs
- [ ] WebCamManager.cs
- [ ] OverlayTextService.cs
- [ ] FfmpegStreamer.cs
- [ ] OverlayMjpegStreamer.cs
- [ ] TimelapseManager.cs
- [ ] TimelapseService.cs
- [ ] Program.cs minimal API endpoints

## Program.cs Special Cases

### Startup Code (before app.Build()):
Use a temporary LoggerFactory:
```csharp
using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var startupLogger = loggerFactory.CreateLogger("PrintStreamer.Startup");
startupLogger.LogInformation("Loading configuration...");
```

### After app.Build():
```csharp
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Application started");
```

### Minimal API Endpoints:
Inject ILogger into route handlers:
```csharp
app.MapPost("/api/endpoint", async (HttpContext ctx, ILogger<Program> logger) =>
{
    logger.LogInformation("Endpoint called");
    return Results.Ok();
});
```

### PrintHelp():
Keep Console.WriteLine for help text since it's meant for console output.

## Search and Replace Patterns

1. Find: `Console.WriteLine($"[ServiceName] {message}");`
   Replace: `_logger.LogInformation(message);`

2. Find: `Console.WriteLine($"Error: {ex.Message}");`
   Replace: `_logger.LogError(ex, "Error occurred");`

3. Find: `Console.WriteLine($"Warning: ...");`
   Replace: `_logger.LogWarning("...");`

## Testing

After refactoring:
1. Build the project to ensure no compilation errors
2. Run the application and verify log output appears
3. Check log levels are appropriate (Info vs Debug vs Warning vs Error)
4. Verify structured logging parameters are captured correctly

## Benefits

- Structured logging with proper log levels
- Better integration with logging frameworks (Serilog, NLog, etc.)
- Log filtering and routing capabilities
- Exception details automatically included
- Performance (structured logging is more efficient than string interpolation)
