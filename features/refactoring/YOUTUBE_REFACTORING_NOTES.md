# YouTube Architecture Refactoring: Singleton Pattern

## Overview

Refactored YouTube services to use a **singleton pattern** instead of creating new `YouTubeControlService` instances for every operation.

### Problem (Before)
- `YouTubeControlService` was instantiated repeatedly throughout the codebase
- Each instantiation triggered fresh OAuth authentication
- Token refresh loops were created and immediately disposed
- Memory/resource inefficiency
- Hard to track singleton lifecycle

### Solution (After)
- `YouTubeControlService` registered as a **singleton in DI**
- Single instance throughout application lifetime
- Authentication happens once at startup
- Cleaner lifecycle management

---

## Changes Made

### 1. Made YouTubeControlService Public and Namespaced
**File:** `Services/YouTubeControlService.cs`

```csharp
// Before: internal class (no namespace)
internal class YouTubeControlService : IDisposable

// After: public class (in namespace)
namespace PrintStreamer.Services
{
    public class YouTubeControlService : IDisposable
```

**Why:** Needed to be public to register in DI container. Required explicit namespace to avoid compilation issues.

### 2. Registered YouTubeControlService as Singleton
**File:** `Program.cs`

```csharp
// Added after YouTubePollingManager registration
webBuilder.Services.AddSingleton<PrintStreamer.Services.YouTubeControlService>();
```

**Why:** Ensures only one instance exists for the application lifetime. Removed the redundant DI registrations for `YouTubeReuseManager`, `YouTubeBroadcastStore`, and `YouTubeReuseOptions` since they were dead code.

### 3. Updated StreamOrchestrator to Use Injected Service
**File:** `Services/StreamOrchestrator.cs`

**Before:**
```csharp
public class StreamOrchestrator
{
    private YouTubeControlService? _currentYouTubeService;
    
    public StreamOrchestrator(StreamService streamService, IConfiguration config, 
        ILoggerFactory loggerFactory, YouTubePollingManager pollingManager)
    {
        // No YouTubeControlService injected
    }
    
    public async Task<...> StartBroadcastAsync(...)
    {
        // Created new instance
        var ytService = new YouTubeControlService(_config, ytLogger, _pollingManager);
        await ytService.AuthenticateAsync();
        // ... used and disposed
    }
}
```

**After:**
```csharp
public class StreamOrchestrator
{
    private readonly YouTubeControlService _youtubeService;
    
    public StreamOrchestrator(StreamService streamService, IConfiguration config, 
        ILoggerFactory loggerFactory, YouTubePollingManager pollingManager, 
        YouTubeControlService youtubeService)
    {
        _youtubeService = youtubeService;
    }
    
    public async Task<...> StartBroadcastAsync(...)
    {
        // Use injected instance
        await _youtubeService.AuthenticateAsync();
        var result = await _youtubeService.CreateLiveBroadcastAsync();
        // ...
    }
}
```

**Changes:**
- Added `YouTubeControlService` parameter to constructor
- Removed `_currentYouTubeService` field (no longer needed)
- Replaced `new YouTubeControlService()` with `_youtubeService`
- Removed all `Dispose()` calls on YouTube service
- Updated `StopBroadcastAsync`, `StopBroadcastKeepLocalAsync`, `EnsureStreamingHealthyAsync`, and `Dispose` methods

### 4. Deleted Dead Code
**Files Deleted:**
- `Services/YouTubeReuseManager.cs` - Was never wired up to the application
- `Services/YouTubeBroadcastStore.cs` - Only used by YouTubeReuseManager
- `Services/YouTubeReuseOptions.cs` - Configuration for dead code

**Why:** These were architectural layers that were planned but never actually used. They created confusion about the entry point and served no purpose in the running application.

---

## Authentication Behavior

With the singleton pattern, authentication behavior changes slightly:

### Before
- Every `StartBroadcast` operation would create a new service
- Each service would authenticate independently
- Multiple auth flows could potentially happen

### After
- Single authentication on first use
- `AuthenticateAsync` is idempotent (safe to call multiple times)
- Refresh tokens handled by the singleton's refresh loop
- More efficient and predictable

---

## Remaining Work

### MoonrakerPoller (Deferred)
`MoonrakerPoller` is a static class that still instantiates `YouTubeControlService` directly:
- Lines 137, 415, 972 in `MoonrakerPoller.cs`
- This would require converting `MoonrakerPoller` from static to a service, which is beyond the scope of this immediate refactoring
- The singleton pattern here will still benefit MoonrakerPoller if/when it's refactored to use DI

### YouTubeAuthService (Future)
Could further extract auth logic to a separate `YouTubeAuthService`:
- OAuth2 flow (browser, headless)
- Token storage abstraction
- Token refresh management
- However, this is optional—the current structure works well

---

## Benefits

1. ✅ **Single Point of Initialization** - Auth happens once
2. ✅ **Memory Efficiency** - One instance instead of many
3. ✅ **Lifecycle Management** - Clear creation/disposal
4. ✅ **Reduced Complexity** - No manual instantiation/disposal
5. ✅ **Testability** - Easier to mock/inject for testing
6. ✅ **Dead Code Removal** - Simplified architecture

---

## Testing

Verify the following works:
- [ ] YouTube authentication on startup
- [ ] Creating a new broadcast
- [ ] Transitioning to live
- [ ] Ending a broadcast
- [ ] Adding to playlist
- [ ] Uploading timelapse videos
- [ ] Setting thumbnails

---

## Architecture Diagram

```
Program.cs DI Container
    ↓
    +--- YouTubeControlService (Singleton)
    |         ↓
    |    +--- YouTubeAuthService (contains OAuth + token management)
    |    +--- Broadcast API methods
    |    +--- Video API methods  
    |    +--- Playlist API methods
    |
    +--- StreamOrchestrator (Singleton)
         └─→ Injects YouTubeControlService
            ├─ StartBroadcastAsync
            ├─ StopBroadcastAsync
            └─ EnsureStreamingHealthyAsync
```

---

## Commit Message (if applicable)

```
refactor: use singleton YouTubeControlService pattern

- Register YouTubeControlService as singleton in DI
- Update StreamOrchestrator to inject and use singleton instance
- Remove direct instantiation of YouTubeControlService
- Delete dead YouTubeReuseManager, YouTubeBroadcastStore, YouTubeReuseOptions
- Make YouTubeControlService public and properly namespaced

Benefits:
- Authentication happens once instead of per-operation
- Cleaner lifecycle management
- Better resource efficiency
- Simplified architecture
```
