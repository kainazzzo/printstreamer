# Timelapse Finalization: Proposal to Remove Duplicate-Finalize Risk

Status: proposal — no code changes applied yet.

Summary
- Problem: Two places can attempt to finalize a timelapse: TimelapseManager.NotifyPrintProgressAsync (manager-side auto-finalize) and callers (orchestrator / API) that invoke StopTimelapseAsync. Historically this produced duplicate-finalize regressions.
- Goal: Make finalization deterministic and configurable so only one component performs the final Stop/CreateVideo operation while preserving backwards compatibility and tests.

Design options (brief)
- Option A — Manager-centralized finalization
  - TimelapseManager remains or becomes the single authority: it auto-finalizes when threshold hit.
  - Callers must never call StopTimelapseAsync directly for auto-finalized sessions.
  - Pros: single place for finalization logic; less orchestration code.
  - Cons: callers that currently expect to control lifecycle must be updated; larger refactor of orchestrator/tests.

- Option B — Orchestrator-centralized finalization (recommended)
  - TimelapseManager *signals* the last-layer condition but does not perform Stop/CreateVideo automatically unless explicitly enabled.
  - Orchestrator (or API) performs the StopTimelapseAsync when it decides (allows retry/backoff / telemetry).
  - Pros: keeps lifecycle control in the component that already coordinates streams and uploads; easier to handle DI/regression risk.
  - Cons: requires small updates in TimelapseManager and callers to check the signal, and an opt-in config flag to preserve current behavior.

Recommendation
- Implement Option B by adding a config toggle and small behavioral change in TimelapseManager:
  - Add config key `Timelapse:AutoFinalize` (bool). Default = true to preserve current behavior.
  - Change NotifyPrintProgressAsync to:
    - Compute trigger as now.
    - If threshold reached:
      - If `Timelapse:AutoFinalize == true`: call `StopTimelapseAsync(sessionName)` as today (backward-compatible).
      - Else: mark session.IsStopped = true (or set a new session.Flag `LastLayerReached = true`) and return a sentinel (null or explicit signal). Do NOT call StopTimelapseAsync.
- Update callers (orchestrator & tests) to:
  - Prefer `await _timelapseManager.NotifyPrintProgressAsync(...)` where orchestration needs the result.
  - If NotifyPrintProgressAsync returns a non-null videoPath, treat it as "manager finalized".
  - If it returns null and `Timelapse:AutoFinalize == false`, orchestration should call `StopTimelapseAsync(sessionName)` when appropriate (e.g., job end) and set internal guard `_timelapseFinalizedForJob` to avoid duplicates.

Concrete code changes (suggested snippets)

1) TimelapseManager.NotifyPrintProgressAsync (replace the internal finalize call)

Replace:
```csharp
// Create video synchronously via existing StopTimelapseAsync to ensure proper cleanup
var createdVideo = await StopTimelapseAsync(sessionName);
return createdVideo;
```

With:
```csharp
var autoFinalize = _config.GetValue<bool?>("Timelapse:AutoFinalize") ?? true;
if (autoFinalize)
{
    var createdVideo = await StopTimelapseAsync(sessionName);
    return createdVideo;
}
else
{
    // Signal to callers that we reached the last-layer threshold: stop capturing but do not create video here.
    session.IsStopped = true;
    // Optionally set an explicit flag for callers:
    // session.LastLayerReached = true;
    if (_verboseLogs) Console.WriteLine($"[TimelapseManager] Threshold reached for {sessionName}; AutoFinalize disabled - caller must call StopTimelapseAsync.");
    return null;
}
```

2) PrintStreamOrchestrator (consumer-side handling)

- Where orchestration currently calls the synchronous `NotifyPrintProgress(...)` or directly calls `StopTimelapseAsync(...)`, change to call `NotifyPrintProgressAsync` when layer updates arrive and check the result:

Example:
```csharp
var createdByManager = await _timelapseManager.NotifyPrintProgressAsync(_activeTimelapseSession, current.CurrentLayer, current.TotalLayers);
if (!string.IsNullOrWhiteSpace(createdByManager))
{
    // Manager already finalized. Record finalized job and skip calling Stop again.
    _timelapseFinalizedForJob = state.Filename;
}
else
{
    // If AutoFinalize disabled, orchestrator should call StopTimelapseAsync at the appropriate job-end event.
    // Existing orchestrator flow that detects job-complete should call:
    var createdVideoPath = await _timelapseManager.StopTimelapseAsync(sessionName);
    if (createdVideoPath != null) _timelapseFinalizedForJob = state.Filename;
}
```

3) Add config docs / defaults
- appsettings.json (documentation only; do not force change in repo unless approved):
```json
"Timelapse": {
  "AutoFinalize": true,
  "StartAfterLayer1": true,
  "LastLayerOffset": 1,
  "Period": "00:01:00",
  "MainFolder": "timelapse",
  "VerboseLogs": false
}
```

Tests to update
- TimelapseManagerTests.cs
  - Add tests for both `AutoFinalize = true` and `AutoFinalize = false` behaviors.
  - When AutoFinalize=false, NotifyPrintProgressAsync should not remove session (Stop not called), and session.IsStopped should be true.
- PrintStreamOrchestratorTests.cs
  - Ensure orchestrator handles both cases:
    - If manager returns a videoPath, orchestrator must not call StopTimelapseAsync again.
    - If manager returns null and AutoFinalize=false, orchestrator must call StopTimelapseAsync once when job completes.

Migration plan (step-by-step)
1. Create `Timelapse:AutoFinalize` config (default true) and document it.
2. Update TimelapseManager.NotifyPrintProgressAsync as shown above.
3. Update orchestration code paths:
   - Replace synchronous `NotifyPrintProgress` calls with `NotifyPrintProgressAsync` where callers expect auto-finalize feedback, or call both depending on performance requirements (notify then stop).
4. Update unit tests to cover both config modes.
5. Run unit tests and integration tests locally.
6. Rollout/default: no behavioral change as default is true; if you want orchestrator to own finalization, set `Timelapse:AutoFinalize` = false in deployment config and verify orchestration calls `StopTimelapseAsync` on job end.

Edge cases and notes
- Race window: both manager and orchestrator could still race if they run concurrently and `AutoFinalize` is true. The `_timelapseFinalizedForJob` guard in orchestrator should remain to prevent restarts; keep it.
- DI / registration regressions: avoid changing constructors or service lifetimes during this change. The work is behavioral and config-driven; no DI changes required.
- Backwards compatibility: defaulting `AutoFinalize` to true keeps existing behavior for users who don't change config.
- Telemetry: prefer having orchestration set `_timelapseFinalizedForJob` when it performs finalization, or respect manager return value when manager finalizes.

Rollback plan
- If regressions appear, revert the NotifyPrintProgressAsync change and revert to previous behavior (manager auto-finalizes) — tests will catch regressions.

CI / verification
- Run `dotnet test` for tests under `tests/PrintStreamer.Utils.Tests`.
- Add new unit tests covering both config branches.

Next step I can take (pick one; I will perform one tool call):
- Implement the suggested code change in TimelapseManager.cs (small replace_in_file).
- Implement config key addition and update any relevant docs file (write_to_file).
- Run a focused search to list current callers of NotifyPrintProgressAsync/NotifyPrintProgress/StopTimelapseAsync (already done) and produce a callers map (write_to_file).
- Create the test changes scaffolding in tests/.. to cover both AutoFinalize values (write_to_file).
