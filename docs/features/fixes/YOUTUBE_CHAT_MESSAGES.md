# YouTube Live Chat Integration

## What Changed
- Added a cached lookup for the live chat ID when a broadcast is created.
- New helper `YouTubeControlService.SendChatMessageAsync` sends messages via the YouTube Live Chat API.
- `StreamOrchestrator` now posts an optional welcome message automatically once the broadcast is fully live.
- Exposed `/api/live/chat` endpoint so the UI or external tools can push arbitrary chat messages while a broadcast is active.

## Configuration
Add an optional welcome string to `appsettings.*.json`:

```json
"YouTube": {
  "LiveBroadcast": {
    "WelcomeMessage": "👋 Thanks for watching!" 
  }
}
```

If `WelcomeMessage` is omitted or empty, no automatic chat message is posted.

## API
`POST /api/live/chat`

```json
{
  "message": "Layer change coming up!"
}
```

- Requires an active broadcast.
- Returns `{ "success": true }` when the message is accepted by YouTube.
- All failures are reported with `{ "success": false, "error": "..." }`.
