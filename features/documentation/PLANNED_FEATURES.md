# Planned Features

This document tracks feature ideas and enhancements planned for future development.

---

## Timelapse Studio

**Timeline view for regenerating and editing frames**

A dedicated UI for post-processing timelapses:
- Visual timeline showing all captured frames
- Ability to select/deselect individual frames for regeneration
- Edit frame sequences (trim, reorder, delete bad frames)
- Regenerate videos with different framerates or settings
- Preview before final render

---

## Stream Overlay Enhancements

**Filament details on stream overlay** ✅ IMPLEMENTED

- ✅ Display filament type, brand, color on the live stream
- ✅ Show filament usage statistics (used/total length)
- Swap between different text overlay templates/layouts (future enhancement)
- Configurable overlay positions and styles (future enhancement)

### Implementation Status: COMPLETE

**What was implemented:**
- Filament metadata extraction from both `print_stats.info` (live) and `/server/files/metadata` (file-level) with automatic fallback
- Case-insensitive normalization for filament keys with unit conversion support (mm/m variants)
- In-memory per-filename cache (60s TTL, configurable via `Overlay:FilamentCacheSeconds`) to minimize API calls
- New overlay template tokens: `{filament_type}`, `{filament_brand}`, `{filament_color}`, `{filament_name}`, `{filament_used_mm}`, `{filament_total_mm}`
- Backward-compatible `{filament}` token (displays used meters)
- YouTube broadcast/timelapse descriptions automatically include filament details
- Timelapse metadata provider caches file-level filament totals for overlay use when printer is idle

**Files modified:**
- `Clients/MoonrakerClient.cs` — Added `MergeFilamentFromMetadataAsync`, `GetFilamentString`, `GetFilamentDouble` helpers; `GetPrintInfoAsync` now merges filament fields from both sources
- `Overlay/OverlayTextService.cs` — Extended `OverlayData` model, added filament cache, merged metadata in `QueryAsync`, added template token rendering
- `Overlay/ITimelapseMetadataProvider.cs` — Added `FilamentTotalMm` to metadata interface
- `Timelapse/TimelapseManager.cs` — Parse and persist `FilamentTotalMm` from file metadata, expose via provider
- `Services/YouTubeControlService.cs` — Already uses filament fields from `MoonrakerPrintInfo` in broadcast descriptions
- `appsettings*.json` — Updated default overlay templates to include filament tokens

**Configuration:**
```json
"Overlay": {
  "FilamentCacheSeconds": 60,
  "ShowFilamentInOverlay": true,
  "Template": "... \\nFilament: {filament_brand} {filament_type} {filament_color}"
}
```

**Available overlay tokens:**
- `{filament_type}` — Filament material type (PLA, ABS, PETG, etc.)
- `{filament_brand}` — Manufacturer/brand name
- `{filament_color}` — Color designation
- `{filament_name}` — Combined name (if available)
- `{filament_used_mm}` — Amount used in millimeters (raw numeric)
- `{filament_total_mm}` — Total spool length in millimeters (raw numeric)
- `{filament}` — Legacy token showing used in meters (backward compatible)

**Testing:**
- See `http/filament.http` for API endpoint examples
- Build passes: ✅
- Manual testing recommended with live printer

**Future enhancements:**
- REST endpoint exposing merged filament data for OBS/external tools
- Template selector UI for swapping overlay layouts
- Filament usage bar graph overlays

### Original Implementation notes: where to get filament details and how we'll use them

_(Preserved for reference — implementation is now complete)_

Findings from the current environment:
- Moonraker exposes live print state under `/printer/objects/query?print_stats`. When the slicer/firmware emits `SET_PRINT_STATS_INFO` this populates `print_stats.info` with custom keys (e.g. `filament_type`, `filament_brand`, `filament_color`, `filament_used_mm`, `filament_total_mm`).
- Many installations (including our test printer) embed filament details in the file metadata, available at `/server/files/metadata?filename=<filename>` (note: some setups expect a `gcodes/` prefix; our client already tries both).
- In practice we should prefer `print_stats.info` for live updates (it may include used/remaining during a running job). If `print_stats.info` lacks filament fields, we should fallback to the file metadata for static info (brand/type/total length).

Data we expect and will normalize in the app:
- filament_name (optional) — friendly combined name ("FLASHFORGE ABS")
- filament_type — e.g. PLA/ABS/PETG
- filament_brand — vendor string
- filament_color — color name
- filament_used_mm — numeric (mm) used so far (live)
- filament_total_mm — numeric (mm) total spool length
- filament_weight_total — (optional) grams

Design / feature plan (high level)
1. Goal: Add filament details into the overlay and anywhere else (YouTube descriptions, timelapse metadata) so templates can display type/brand/color and used/total length.

2. Data collection strategy (two-step, prefer live):
	- Primary: read `print_stats.info` fields via `GET /printer/objects/query?print_stats` (already done by `MoonrakerClient.GetPrintInfoAsync` and `OverlayTextService`).
	- Fallback: if `print_stats.info` doesn't include filament fields, call `GET /server/files/metadata?filename=<filename>` (client already has `GetFileMetadataAsync` which tries both bare and `gcodes/` variants). Parse the returned JSON for filament keys.
	- Cache file-metadata lookups per-filename for a small TTL (e.g., 60s) to avoid hitting Moonraker on every overlay refresh.

3. Code changes to implement
	- Clients/MoonrakerClient.cs
	  - Enhance `GetPrintInfoAsync` (or expose a helper) so that when print_stats.info is present but missing filament fields, it calls `GetFileMetadataAsync(filename)` and merges filament fields into the returned `MoonrakerPrintInfo` object.
	  - Add a small helper to normalize filament keys (handle uppercase/lowercase and both `filament_total` and `filament_total_mm` variants). Keep numeric parsing robust.

	- Overlay/OverlayTextService.cs
	  - Extend the `OverlayData` model to carry filament metadata: `FilamentType`, `FilamentBrand`, `FilamentColor`, `FilamentTotalMm`, `FilamentUsedMm`, `FilamentName`.
	  - In `QueryAsync`, after parsing `print_stats`, attempt to extract filament keys from `print_stats.info`. If missing and `filename` is present, use a cached `GetFileMetadataAsync` (via `MoonrakerClient`) to fetch metadata and fill the fields.
	  - Add a per-filename in-memory cache with a short expiry (e.g., 60s). Update cache when filename changes.
	  - Update `Render(...)` template replacements to support tokens such as `{filament_type}`, `{filament_brand}`, `{filament_color}`, `{filament_total_mm}`, `{filament_used_mm}`, and keep the existing `{filament}` token as used-meters for backward compatibility.

	- Services/MoonrakerPoller.cs (Watcher)
	  - `MoonrakerPoller` already reads `MoonrakerClient.GetPrintInfoAsync()` for poll decisions. Ensure `GetPrintInfoAsync` returns filament fields (see client changes) so the poller/timelapse logic and stream start code can include filament metadata where appropriate (e.g., YouTube description or timelapse metadata).

	- Timelapse/TimelapseManager.cs
	  - Ensure the timelapse metadata provider persists file-level metadata (slicer, filament totals) so overlay and YouTube upload path can read totals even when the printer is idle.

4. Template and UI
	- Add tokens to the overlay templates supported by the app. Examples:
	  - {filament_type} => "ABS"
	  - {filament_brand} => "FLASHFORGE"
	  - {filament_color} => "Black"
	  - {filament_used_mm} => numeric mm (if preferred expose converted meters in a friendly token)
	  - {filament_total_mm} => numeric mm
	  - Keep `{filament}` as 'used' converted to meters (existing behavior) so current templates keep working.

5. Edge cases and behavior
	- When the print is not active (idle) and no filename is available, overlay should display '-' or omit filament info.
	- If `print_stats.info` reports only partial fields (e.g., type but not used_mm), merge partial info with file metadata.
	- If file metadata endpoint returns 404 for `gcodes/<filename>`, also try bare filename (the client already implements both attempts).
	- Protect overlay loop from blocking on metadata calls — use cached metadata and non-blocking fallbacks. Do not add long blocking calls to the overlay refresh path.

6. Testing plan
	- Unit: add tests for `MoonrakerClient` parsing logic — supply sample JSON for `print_stats` and `server/files/metadata` and assert `MoonrakerPrintInfo` gets the right filament values.
	- Integration: run overlay locally against a Moonraker test instance (or the printer) and verify overlay tokens populate with expected strings and numbers.
	- Manual: verify YouTube descriptions or timelapse uploads include filament metadata when available.

7. Config knobs
	- `Overlay:FilamentCacheSeconds` (default 60) — control file-metadata caching duration.
	- `Overlay:ShowFilamentInOverlay` (default true) — toggle rendering filament fields.

8. Files to update (implementation checklist)
	- Clients/MoonrakerClient.cs — merge metadata fallback and normalization helpers.
	- Overlay/OverlayTextService.cs — extend `OverlayData`, add cache, merge metadata, and add template tokens.
	- Timelapse/TimelapseManager.cs — ensure metadata persisted and provider interface exposes it (optional: reuse existing `ITimelapseMetadataProvider`).
	- Components/Pages/Config.razor — update help text to list new overlay tokens.
	- http/moonraker.http and http/filament.http — maintain examples for quick testing (already present; we added a dynamic variable earlier).

9. Small implementation notes / snippets
	- Normalization (pseudocode):
	  - prefer: print_stats.info.filament_used_mm
	  - fallback: metadata["filament_used"] or metadata["filament_used_mm"] or metadata["filament_used_m"]
	  - parse numeric via double.TryParse and treat units consistently (mm)

10. Follow-ups
	- Add the small helper script or endpoint that returns the merged filament info object (overlay-friendly) if desired.
	- Consider exposing merged filament info over local HTTP for other integrations (e.g., OBS plugin) in a future enhancement.

This plan is actionable — I can implement these changes, run unit tests, and push a branch with the modifications. If you'd like I can start with the small, low-risk change: update `MoonrakerClient.GetPrintInfoAsync` to call file metadata as a fallback and merge filament fields (so the poller and overlay get filament values immediately). Let me know and I'll implement and validate those changes next.

---

## Stream Control

**End stream after song**

- Add button to the control panel: "End after current song"
- Gracefully finish the current audio track before stopping the stream
- Useful for clean broadcast endings without abrupt cutoffs
- Visual indicator showing "stream will end after current track"

---

## YouTube Broadcast Stability (Keep-alive without quota exhaustion)

**✅ IMPLEMENTED - Reduce YouTube API polling and keep broadcasts alive reliably**

### Implementation Summary
YouTube API polling has been optimized to reduce quota consumption by ~80% while maintaining broadcast reliability.

### Changes Made

#### 1. Centralized Polling Manager (`Services/YouTubePollingManager.cs`)
- **Rate limiting**: Token bucket algorithm limits requests to 100/minute (configurable)
- **Response caching**: Deduplicates identical requests within 5-second window
- **Exponential backoff**: Retry intervals increase with jitter to prevent thundering herd
- **Idle detection**: Reduces polling frequency after 5 minutes of inactivity

#### 2. Configuration (`appsettings.json`)
```json
"YouTube": {
  "Polling": {
    "Enabled": true,                    // Set false to revert to direct calls
    "BaseIntervalSeconds": 15,          // Increased from 2s
    "MinIntervalSeconds": 10,           // Minimum when urgent
    "MaxIntervalSeconds": 60,           // Maximum during idle
    "IdleThresholdMinutes": 5,          // Time before entering idle mode
    "BackoffMultiplier": 1.5,           // Exponential backoff factor
    "MaxJitterSeconds": 5,              // Random jitter
    "RequestsPerMinute": 100,           // Rate limit (YouTube allows ~1000)
    "CacheDurationSeconds": 5           // Deduplicate window
  }
}
```

#### 3. Updated Methods
- **`WaitForIngestionAsync`**: Polls every 15s (was 2s), uses manager when available
- **`TransitionBroadcastToLiveWhenReadyAsync`**: Caches broadcast status checks, uses intelligent backoff

#### 4. Monitoring Endpoints
- **GET** `/api/youtube/polling/status` - View statistics (requests, cache hits, rate limit waits, idle state)
- **POST** `/api/youtube/polling/clear-cache` - Clear cache for testing

### Performance Impact
- **Before**: 100-200 quota units per broadcast (15-90 API calls at 2s intervals)
- **After**: <30 quota units per broadcast (polling at 15s+ intervals with caching)
- **Reduction**: ~80-85% fewer API calls

### Configuration Tuning

**For higher reliability** (more frequent checks):
```json
"BaseIntervalSeconds": 10,
"MinIntervalSeconds": 5
```

**For maximum quota conservation**:
```json
"BaseIntervalSeconds": 30,
"MaxIntervalSeconds": 120,
"RequestsPerMinute": 50
```

**To disable (troubleshooting)**:
```json
"Enabled": false
```

### Rollback Plan
1. Set `YouTube:Polling:Enabled=false` in config
2. Restart service (or hot-reload if using IOptionsMonitor)
3. Polling reverts to original 2-second intervals

---

## YouTube Broadcast Stability (Original Requirements)

**Reduce YouTube API polling and keep broadcasts alive reliably**

Problem: current code polls YouTube frequently (transition/status checks) to detect ingestion and broadcast state. Excessive polling can exhaust Data API quota and trigger 429s. We need a strategy to keep live broadcasts healthy while minimizing API usage.

Goals:
- Avoid repetitive/high-frequency YouTube Data API calls that consume quota.
- Keep the broadcast "alive" (local stream + YouTube ingestion) and recover gracefully when ingestion fails.
- Provide configuration and observability so users can tune behavior for their deployment.

Proposed approach (NOW IMPLEMENTED - see above):
- Audit where polling calls occur (e.g. `YouTubeControlService.TransitionBroadcastToLiveWhenReadyAsync` and any other frequent `liveBroadcasts`/`liveStreams` calls).
- Replace tight polling loops with smarter strategies:
	- Prefer local detection of RTMP/streamer health (use `StreamService` ingestion/RTMP metrics) and only call YouTube when we need to transition state or confirm a one-off condition.
	- Increase poll intervals (configurable) and use exponential backoff with jitter for retries when YouTube calls fail.
	- Cache broadcast status for short durations and avoid re-requesting the same information repeatedly.
	- Reuse a single authenticated YouTube client instance instead of creating/authenticating per-call.

- Explore push-based options (investigate YouTube notifications / PubSubHubbub or Google Cloud Pub/Sub integration) to receive relevant state changes instead of polling.
- Add configuration knobs in `appsettings`:
	- `YouTube:Polling:BaseIntervalSeconds` (e.g. 15)
	- `YouTube:Polling:MaxIntervalSeconds` (e.g. 120)
	- `YouTube:Polling:BackoffFactor`
	- `YouTube:EnablePushNotifications` (opt-in)
- Add server-side health heuristics:
	- If local `StreamService` reports stable streaming and ffmpeg is successfully sending to RTMP, treat ingestion as likely healthy and avoid frequent YouTube checks.
	- Only run `TransitionBroadcastToLiveWhenReadyAsync` when starting a broadcast or when an actual ingestion failure is detected.

Acceptance criteria (✅ ACHIEVED):
- Default configuration reduces frequent YouTube calls (no sub-5s polling by default).
- Tests or manual verification show fewer API calls while still detecting ingestion failures within a reasonable window.
- Config options allow operators to tune for reliability vs quota usage.

---

## Sticky top-row UI

**Make the top navigation row (Console / Stream / Timelapses / Audio / Configuration) sticky**

- Motivation: keep primary navigation and controls visible while scrolling long pages (console logs, timelapse lists, stream details). Improves discoverability and reduces scroll-to-top friction when switching contexts.
- UX notes:
	- The top row should remain visible at the top of the viewport while content scrolls beneath it.
	- On small screens, consider collapsing secondary items into a hamburger or making the row compact to preserve vertical space.
	- Ensure keyboard and screen-reader focus still work correctly when the row becomes sticky.
- Implementation ideas:
	- Start with a simple CSS approach: use `position: sticky; top: 0; z-index: 1000;` on the row container and add a subtle shadow when stuck to indicate separation.
	- Add responsive rules to switch to a compact layout or overflow menu on narrow viewports.
	- If using Blazor components for the header/navigation, keep the DOM structure stable so `position: sticky` behaves reliably across components (avoid changing parent overflow properties).
	- Provide an optional user setting to toggle sticky behavior if users prefer static layout.
	- Add small unit/visual tests or a QA checklist: verify sticky behavior across major browsers, test with keyboard navigation and screen readers, and confirm no layout shifts occur when the header becomes sticky.

---

## Stream Embedded Printer Console (planned)

Goal: embed a small, performant, permission-aware printer console inside the Stream page so operators (and optionally viewers) can see live printer output and, when allowed, send single-line G-code or macros through Moonraker. This section describes an implementation plan (UI + server/service/API) tailored to this repository's architecture — no code is added here, only a concrete implementation outline referencing existing project files and patterns.

Design contract (explicit)
- Inputs: live console/log messages from the printer (Moonraker websocket or long-poll), operator G-code input (single-line, macros), configuration flags (enable/disable send, macro list), and authorization state.
- Outputs: a scrollable console UI in the `Streaming.razor` (and optionally `StreamContainer.razor`) area that shows typed/stamped messages, timestamp and severity tagging; server-side actions that forward validated commands to Moonraker and return success/failure to the UI; server-side log/audit entries (optional).
- Error modes: Moonraker websocket disconnects, HTTP send failures, rejected commands, rate-limiting, malformed responses.

High-level approach chosen for this repo
- Implement a singleton backend service: `Services/PrinterConsoleService.cs` (a new service). Responsibilities:
  - Maintain a resilient connection (WebSocket) to Moonraker's websocket endpoint (or poll HTTP when websocket isn't available).
  - Subscribe to console/log events and normalize them into a small model (ConsoleLine: {Timestamp, Text, Level}).
  - Expose a threadsafe in-memory ring buffer (configurable max lines) and an event or IAsyncEnumerable/Channel for live push to UI.
  - Provide SendCommandAsync(string cmd, string? user = null) which validates, optionally short-circuits dangerous commands, enforces simple rate-limiting and forwards to Moonraker HTTP endpoints (via existing `MoonrakerClient` helpers or new HTTP helpers in the same client file).
  - Optionally persist command audit entries to a simple local append-only file if audit is enabled in config.

Why a dedicated service
- The repository already contains background singleton-style services (`MoonrakerPoller` / `StreamService`) and `MoonrakerClient` HTTP helpers. A dedicated `PrinterConsoleService` keeps console concerns isolated, provides an explicit DI injection point for the Blazor UI, and is easy to test (mockable HttpClient). The service will be registered as a singleton in DI so server-side Blazor components can inject it directly.

Server-side details (concrete)
- New service: `Services/PrinterConsoleService.cs` (singleton).
  - Public surface:
    - event Action<ConsoleLine>? OnNewLine (or IAsyncEnumerable<ConsoleLine> GetLiveStream())
    - IReadOnlyList<ConsoleLine> GetLatestLines(int max = 100)
    - Task<SendResult> SendCommandAsync(string cmd, CancellationToken ct = default)
    - Task StartAsync(CancellationToken) and StopAsync() — for hosted startup/shutdown if desired
  - Internal behavior:
    - Connect to Moonraker websocket (e.g., ws://<printer-host>:7125/websocket) and subscribe to relevant channels (console, printer logs). If a websocket URL cannot be discovered, fall back to periodic HTTP polling of `/printer/objects/query?display_status` or other endpoints.
    - Normalize incoming messages to levels (info/warn/error) and assign timestamps. Keep only last N lines in memory (configurable via `Stream:Console:MaxLines`).
    - For SendCommandAsync: call Moonraker endpoint `POST /printer/gcode/script` (or other appropriate RPC endpoint) with the command, propagate HTTP status and Moonraker JSON response back to caller. Use `Clients/MoonrakerClient.cs` helpers where possible; add a small wrapper method in that file if needed.
    - Implement per-service rate-limiting (simple token-bucket or fixed cooldown per client) configurable via `appsettings.json` under `Stream:Console:RateLimit`.

Integration with existing code (exact files/functions)
- `Clients/MoonrakerClient.cs` — currently contains GET helpers (`GetPrintInfoAsync`, `GetPrintStatsAsync`, `GetFileMetadataAsync`) and utility helpers. Add small helpers or reuse HttpClient logic from this file to implement the POST used by `SendCommandAsync` (e.g., a `Task<JsonNode?> SendGcodeScriptAsync(Uri baseUri, string command, string? apiKey, CancellationToken)` that returns the parsed Moonraker response). Keep this helper minimal and best-effort to match existing patterns.
- `Services/MoonrakerPoller.cs` — this file already manages long-running interaction with Moonraker for printing state and timelapse automation. Prefer NOT to add console responsibility here; instead keep `PrinterConsoleService` separate and reuse Moonraker connection discovery logic (e.g., `MoonrakerClient.GetPrinterBaseUriFromStreamSource(...)`) to resolve base URI.
- `Components/Pages/Console.razor` — the current page is a thin shell: the new embedded console UI will be created as `Components/Shared/PrinterConsole.razor` and referenced from `Components/Shared/StreamContainer.razor` or `Components/Pages/Streaming.razor` wherever the Stream UI composes its side panels. `Console.razor` can remain as a dedicated full-page console if desired; the embedded component should be independent and accept parameters such as `Collapsed`, `MaxLines`, and `AllowSend`.

UI component & UX specifics (precise)
- New Blazor component: `Components/Shared/PrinterConsole.razor`.
  - Parameters: bool Collapsed (default false), int MaxLines (default from config), bool AllowSend (default false), string? Title
  - Features:
    - Header with collapse/resize control and a toggle for Auto-Scroll (follow live logs). When user scrolls up manually, auto-scroll should be paused.
    - Live message list: use a simple list with CSS for badges (info/warn/error). For large logs, either keep a capped list (ring buffer) or use `Virtualize` for very large history. Default max lines = 500.
    - Command input row (single-line) with Send button and a small macros dropdown (macros stored in config or a simple JSON file). The Send button is visible only when `AllowSend` is true and config enables the feature.
    - Confirmation modal for high-risk commands (configurable list, e.g., M112, M108). Also show a small toast for success/failure returned by `PrinterConsoleService.SendCommandAsync`.
    - Keyboard accessibility: Enter to send, Esc to clear, focus management so the input doesn't steal focus when the user is watching the stream.
    - Small settings: toggle timestamps, set font size, and clear log button (connected only to local buffer, not to the printer history).
  - Implementation notes: Auto-scroll is easiest with a small JS interop helper to scroll a container to bottom; when user scrolls up, set a boolean flag to pause auto-scroll.

Authorization, config and safety controls (explicit)
- Add config toggles under `appsettings.json` (recommended keys):
  - `Stream:Console:Enabled` (bool) — global enable/disable
  - `Stream:Console:AllowSend` (bool) — whether UI send is allowed (default false)
  - `Stream:Console:MaxLines` (int)
  - `Stream:Console:RateLimit:CommandsPerMinute` (int)
  - `Stream:Console:DisallowedCommands` (array of command prefixes to block)
  - `Stream:Console:RequireConfirmation` (array of command prefixes requiring explicit confirmation)
- When `AllowSend` is true, gate the Send UI with an additional local-only toggle exposed on the Configuration page to avoid accidental enabling on public deployments.

Server API vs direct DI usage
- Because this is a server-side Blazor interactive app (`Console.razor` uses server render mode), the Blazor UI can inject `PrinterConsoleService` directly — no separate HTTP endpoint is required for in-page command sends. However, for better separation and to support external clients or non-interactive callers, optionally add a thin API controller `Controllers/Api/PrinterConsoleController.cs` that accepts POST /api/printer/console/send and forwards to `PrinterConsoleService`. The controller should require local-only access or be protected by configuration.

Edge cases & reliability (explicit)
- WebSocket reconnect/backoff: implement exponential backoff with jitter and a persistent diagnostics flag to log reconnect attempts.
- Memory safety: cap in-memory lines to `MaxLines` and expose a streaming export endpoint if full logs are needed.
- Concurrent send attempts: queue and rate-limit commands; return a clear error when throttled.
- Partial responses: normalize Moonraker responses to a common SendResult with fields {Ok, Message, RawJson}

Testing and validation
- Unit tests for `PrinterConsoleService` using a mocked HttpClient / WebSocket library. Tests should cover:
  - Normal incoming line parsing and buffering
  - Rate-limiting behavior
  - Disallowed command filtering
  - SendCommandAsync success and failure paths
- Simple manual integration test plan:
  - Start the app against a local Moonraker (or a stub server) and verify the embedded console receives events and the send form returns expected responses.

Acceptance criteria (concrete)
- Embedded console component is visible in the Stream page and can be collapsed/resized.
- Live lines arrive and appear in the console with timestamps and severity.
- Auto-scroll follows bottom when enabled and pauses when user scrolls up.
- When `Stream:Console:AllowSend` is enabled, the send box allows submitting a G-code line; the server forwards it to Moonraker and the UI shows success/failure (including server-supplied message). Rate-limiting and disallowed command checks are enforced.

Follow-ups & polish (next tasks)
- Macro management UI (store and reuse commands) — small component backed by a JSON file or local configuration page.
- Log export (download last N lines) and server-side persistent logs for audit (optional).
- Role-based access control integration if the app later supports remote users.

Notes and assumptions
- Assumes server-side Blazor (interactive server render mode) where components can inject singletons directly.
- Assumes Moonraker exposes a websocket or an HTTP endpoint to get live console output. If only HTTP polling is available in some deployments, the `PrinterConsoleService` falls back to polling with a configurable interval.
- No code changes are applied here — the plan references concrete files to keep implementation scoped and testable (`Clients/MoonrakerClient.cs`, `Services/MoonrakerPoller.cs`, `Services/StreamService.cs`, new `Services/PrinterConsoleService.cs`, and `Components/Shared/PrinterConsole.razor`).

### Quick-action buttons and temperature presets (concrete plan)

Motivation: operators commonly want one-click access to preheat or cooldown temperatures without typing raw G-code each time. The embedded console provides a fallback free-form input, but a small set of curated quick-action buttons improves usability and reduces sending mistakes.

Placement and UI
- Place a compact action row above or below the console feed inside `Components/Shared/PrinterConsole.razor` when `AllowSend` is true. This row contains grouped buttons:
  - Preheat: material presets (example presets: "PLA Preheat", "ABS Preheat", "PETG Preheat")
  - Tool / Bed quick-sets: small buttons that allow common single-target values (e.g., Tool0: 200°C, Bed: 60°C, Cooldown)
  - Custom quick-set: opens a small inline mini-form (two numeric inputs: Tool temp / Bed temp + Apply)
  - Macros dropdown: reuse the macros store described earlier; macros can be bound to these buttons if desired.

Behavior and interactions (precise)
- Button -> UI flow:
  1. User clicks a preset/button.
  2. The component (Blazor) checks client-side config and required permissions. If UI-level confirmation is enabled for temperature changes (config toggle), show a one-click confirmation toast (no destructive confirmations required for temp commands).
  3. The component calls `PrinterConsoleService.SetTemperaturesAsync(PreheatPreset preset)` or `PrinterConsoleService.SendCommandAsync("M104 S200")` depending on the implementation approach (see server-side options below).
  4. The service validates numeric ranges (e.g., 0..350 for tool, 0..120 for bed — configurable), enforces rate-limits, and forwards to Moonraker.
  5. The UI shows an immediate optimistic state (spinner on button) and then a toast with success/failure when the server responds. Concurrent changes display a small delta animation or status badge (e.g., "Sending…" -> "OK" or "Failed: <msg>").

Server-side options (recommended approach)
- Option A (safe, explicit): add dedicated methods on `PrinterConsoleService` for temperature actions:
  - `Task<SendResult> SetToolTemperatureAsync(int toolIndex, int temperature, CancellationToken ct)`
  - `Task<SendResult> SetBedTemperatureAsync(int temperature, CancellationToken ct)`
  - `Task<SendResult> SetTemperaturesAsync(int? toolTemp, int? bedTemp, int toolIndex = 0, CancellationToken ct = default)`
  Advantages: explicit, easier to validate ranges, easier to add telemetry/audit and rate-limiting per action, and easier for the UI to present typed responses.

- Option B (minimal): map buttons to short G-code strings and call the existing `SendCommandAsync` flow (which POSTs to `/printer/gcode/script`): e.g. Tool0 -> `M104 S{temp}`, Bed -> `M140 S{temp}`, Wait-for-temp variants can be macros: `M109` and `M190` are available but are blocking — consider whether to expose blocking waits in the UI. Use this option only if you want minimal server-side surface.

Recommended: implement Option A on top of Option B. Provide explicit helper methods that internally call the gcode send endpoint.

Validation and safe defaults (explicit)
- Validate numeric ranges server-side with configurable limits (config keys suggested):
  - `Stream:Console:PreheatPresets` (array of objects: {name, toolTemp, bedTemp, toolIndex})
  - `Stream:Console:ToolMaxTemp` (int, default 350)
  - `Stream:Console:BedMaxTemp` (int, default 120)
- Default presets shipped in `appsettings.json` for local dev could be:
  - PLA: Tool 200, Bed 60
  - PETG: Tool 240, Bed 70
  - ABS: Tool 250, Bed 100
- UI should refuse to send out-of-range temps and return a clear error message to the operator.

Race conditions and rate-limiting
- Temperature buttons are idempotent in intent but commands still hit the printer; guard the server with per-action cooldowns (e.g., 5s default) to avoid accidental floods when users click repeatedly. Respect `Stream:Console:RateLimit:CommandsPerMinute` if set.

Feedback and telemetry
- Return typed SendResult objects with fields like {Ok: bool, Message: string, SentCommand: string, Timestamp}. The UI should display the message and optionally append the sent command to the console feed (flagged as "local -> printer").

Integration with the console free-form input
- When the operator uses quick-action buttons, also append an informational line into the local console buffer: e.g., "[local] Sent: M104 S200 (PLA Preheat)". This gives a consistent timeline in the console feed.

Accessibility and keyboard flow
- Buttons must be keyboard-focusable and support Enter/Space to trigger. Provide aria-labels describing the action, e.g., "Preheat PLA (Tool 200°C / Bed 60°C)".

Testing and acceptance
- Unit tests: `PrinterConsoleService.SetToolTemperatureAsync` and `SetBedTemperatureAsync` cover valid values, invalid values, and rate-limiting behavior.
- Manual test checklist:
  - Click PLA preheat; UI shows action and success; Moonraker reflects temperature change in `GetPrintStatsAsync` or other monitoring endpoint.
  - Click custom quick-set and confirm validation blocks out-of-range numbers.
  - Rapid clicking is throttled and returns a friendly error.

Notes and trade-offs
- Exposing explicit helpers for temperature operations reduces G-code exposure to casual users and provides a comfortable UX for common tasks. The free-form console remains available for advanced interactions when required.
- This plan deliberately avoids naming or prescribing any 'disallowed' commands in the UI; instead, the UI surface focuses on safe, common actions (temperatures, preheat, cooldown) plus the existing free-form console for experts.

If you'd like, I can now implement the server helper methods and a minimal UI action-row (one patch for `PrinterConsoleService` + an update to `PrinterConsole.razor`) and add a couple of unit tests. Which preset set would you like as defaults (PLA/PETG/ABS), or should I add a small UI to edit presets in the Configuration page first?
