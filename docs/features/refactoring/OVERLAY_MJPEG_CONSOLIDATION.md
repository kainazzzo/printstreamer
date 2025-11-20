# Overlay MJPEG Consolidation

## Summary
Two implementations produce an MJPEG overlay pipeline:
- `Streamers/OverlayMjpegStreamer.cs` — per-request IStreamer used by `Program.cs` at `/stream/overlay`.
- `Services/OverlayMjpegService.cs` — legacy DI service with a `HandleRequestAsync` method (not referenced by `Program.cs`).

`Program.cs` currently constructs and uses `OverlayMjpegStreamer` for runtime requests; `OverlayMjpegService` is an unused/legacy implementation. Both build nearly identical ffmpeg filter chains (drawbox + drawtext) and spawn `ffmpeg` to stream multipart MJPEG.

## Goal
Consolidate behavior by enhancing the active per-request `OverlayMjpegStreamer` with the useful features found in `OverlayMjpegService` — without introducing a new builder pattern. Keep lifecycle (ExitTask/Stop/Dispose) and per-request semantics intact.

## Key differences to pull from `OverlayMjpegService` into `OverlayMjpegStreamer`
1. Path escaping: use an `esc(string)` helper to escape `fontFile` and `textFile` when embedding into ffmpeg filter strings.
2. Source fallback: if `Overlay:StreamSource` is empty, also fall back to `Stream:Source` (service used `Stream:Source`).
3. BannerFraction clamping: align min/max bounds (0 .. 0.6) and behavior when `Overlay:BoxHeight` is not provided.
4. ffmpeg input flags: adopt robust reconnect/fflags options used by the service (merge with existing streamer flags for best resilience).
5. Stderr handling: merge stderr suppression and throttling logic:
   - Suppress benign messages like "unable to decode APP fields" and "Last message repeated".
   - Throttle repetitive benign warnings (log informationally only once per interval).
   - Log other stderr content as warnings with context.
6. Keep `OverlayMjpegStreamer`’s explicit BoxHeight support and X/Y honoring (more flexible).
7. Harmonize MJPEG output options (q/v, boundary tag) and bounds for quality (`mjpegQ`).

## Implementation steps
- Update `Streamers/OverlayMjpegStreamer.cs`:
  - Add `esc(string)` and use for `fontFile`/`textFile`.
  - If `Overlay:StreamSource` missing, read `Stream:Source` as fallback.
  - Clamp `BannerFraction` as in service and preserve BoxHeight handling.
  - Update ffmpeg input `inputArgs` to include reconnect/fflags options used by the service (merge with existing streamer flags).
  - Replace stderr reader with a version that implements suppression and throttling logic (copy/merge from service).
  - Keep existing IStreamer lifecycle methods unchanged.
- Tests:
  - Add unit tests that validate filter strings for representative configs (escaping, BannerFraction bounds, BoxHeight).
  - Manual smoke tests:
    - `curl http://127.0.0.1:8080/stream/overlay`
    - `curl http://127.0.0.1:8080/stream/overlay/capture`
    - Check that overlay text appears, box placement matches config, and logs suppress benign stderr spam.
- Cleanup:
  - After verification, either mark `Services/OverlayMjpegService.cs` with `[Obsolete]` or remove it. Optionally convert it to a thin adapter that delegates to the updated `OverlayMjpegStreamer` logic if backward compatibility is required.

## Testing & rollout
1. Implement changes locally and run unit tests.
2. Start server in preview mode (`Stream:Local:Enabled`) and perform manual smoke tests.
3. Observe logs for suppressed warnings and check overlay visuals.
4. If all good, decide on removal vs. deprecation for `Services/OverlayMjpegService.cs`.
5. Update documentation and optionally add a short note in `docs/_Sidebar.md` referencing this file under "refactoring".

## Notes
- The per-request `IStreamer` pattern should be preserved because it integrates with existing lifecycle and cancellation handling.
- Keep the more flexible BoxHeight/X/Y support already present in the streamer.
- Ensure proper escaping of paths to avoid ffmpeg filter parsing issues.
