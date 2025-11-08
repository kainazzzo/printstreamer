# Timelapse Duplicate Folder Fix

## Problem Description

The system was creating multiple small timelapse folders during a single print job, with the broadcast ending prematurely at the same time. This was caused by duplicate and conflicting logic for detecting when to finalize timelapses.

## Root Cause

There were **two separate processes** trying to detect the last layer and finalize timelapses:

1. **TimelapseManager.NotifyPrintProgressAsync** (async version)
   - Contains logic to auto-finalize timelapse when last layer is reached
   - Calls `StopTimelapseAsync` internally
   - **Was NOT being called** by MoonrakerPollerService

2. **MoonrakerPollerService's own last-layer detection**
   - Had duplicate threshold detection using time, progress%, and layer count
   - Called `StopTimelapseAsync` directly
   - Created race condition when called repeatedly

### The Race Condition

```
Poll Loop Iteration 1:
  → Detects last layer at 98.5% progress
  → Spawns background task to finalize timelapse
  → Sets activeTimelapseSession = null

Poll Loop Iteration 2 (2 seconds later):
  → Still at 98.5%+ progress (print not quite done)
  → No active session, so starts NEW timelapse
  → Creates folder with _1 suffix

Poll Loop Iteration 3:
  → Detects last layer AGAIN
  → Finalizes second timelapse
  → Repeat...
```

## Solution

### 1. Added Missing Async Method
Added `NotifyPrintProgressAsync` to `Timelapse/TimelapseManager.cs` that:
- Monitors layer progress
- Auto-finalizes timelapse when threshold is reached
- Returns video path if finalized, null otherwise

### 2. Removed Duplicate Logic
Modified `Services/MoonrakerPollerService.cs` to:
- Call `NotifyPrintProgressAsync` instead of synchronous `NotifyPrintProgress`
- Removed the duplicate last-layer detection logic (time/progress%/layer thresholds)
- Let TimelapseManager handle auto-finalization
- Only track if timelapse was finalized to prevent starting new ones

### 3. Single Source of Truth
Now **only** TimelapseManager decides when to finalize based on:
- Layer threshold: `Timelapse:LastLayerOffset` (default: 1 layer before end)
- Configuration is centralized in one place
- No race conditions between multiple detection systems

## Changes Made

### File: `Timelapse/TimelapseManager.cs`
- Added `NotifyPrintProgressAsync` method with auto-finalization logic
- Kept synchronous `NotifyPrintProgress` for backward compatibility (marks stopped but doesn't finalize)

### File: `Services/MoonrakerPollerService.cs`
- Changed from calling `NotifyPrintProgress` to `NotifyPrintProgressAsync`
- Removed duplicate last-layer detection block (40+ lines removed)
- Simplified to check if video path is returned (meaning auto-finalized)
- Properly tracks `lastLayerTriggered` to prevent re-detection

## Configuration

The timelapse finalization is controlled by a single setting:

```json
{
  "Timelapse": {
    "LastLayerOffset": 1  // Finalize N layers before completion
  }
}
```

## Testing Recommendations

1. Start a print with known layer count
2. Monitor logs for "Last-layer threshold reached" message
3. Verify only ONE timelapse folder is created per print
4. Verify broadcast stays active until print completes (if auto-end is disabled)
5. Check that only one video file is generated

## Related Settings

- `YouTube:LiveBroadcast:EndStreamAfterPrint` - Controls if broadcast ends when print finishes
- `Timelapse:LastLayerOffset` - How many layers before end to finalize timelapse
- `Moonraker:VerboseLogs` - Enable detailed polling logs for troubleshooting
