# Token Persistence Debugging Guide

## Changes Made

1. **Added comprehensive logging to `YouTubeTokenFileDataStore`**:
   - `StoreAsync()` now logs when saving token, directory creation, and verification
   - `GetAsync()` now logs when reading token file, parsing, and results

2. **Added logging to `AuthenticateAsync()` flow**:
   - Shows the token file path being used
   - Shows whether token exists before loading
   - Shows what happens during token loading (success/failure)
   - Shows token exchange results

3. **Fixed `run.sh`**:
   - Now creates `~/.printstreamer/tokens/` directory BEFORE starting Docker container
   - This ensures the directory exists on the host for Docker to mount

## Test Steps

### First Run (with INTERACTIVE=1)

```bash
INTERACTIVE=1 ./scripts/run.sh
```

**Watch for these logs:**
1. `[YouTube] Using token file path: /app/data/tokens/youtube_token.json`
2. `[YouTube] Token file exists: False` (should be false on first run)
3. `[YouTubeTokenFileDataStore] GetAsync: Token file does not exist`
4. `Automatic browser launch appears unavailable. Falling back to manual auth flow.`
5. `Enter authorization code: ` (wait for this prompt)
6. **Paste your auth code here**
7. `[YouTube] Successfully exchanged auth code for token (hasRefresh=True, expiresIn=3600)`
8. `[YouTubeTokenFileDataStore] StoreAsync: Saving token to /app/data/tokens/youtube_token.json`
9. `[YouTubeTokenFileDataStore] StoreAsync: Verified token file exists, size: XXX bytes`
10. `[YouTube] Authentication successful`

### Second Run (without INTERACTIVE)

```bash
./scripts/run.sh
```

**Watch for these logs:**
1. `[YouTube] Using token file path: /app/data/tokens/youtube_token.json`
2. `[YouTube] Token file exists: True` (should be true now!)
3. `[YouTubeTokenFileDataStore] GetAsync: Read token file (XXX bytes)`
4. `[YouTubeTokenFileDataStore] GetAsync: Loaded TokenResponse with RefreshToken=True`
5. `[YouTube] Loaded token from store: yes`
6. `[YouTube] Found existing refresh token in store, using it for authentication`
7. `[YouTube] Authentication successful`

## What Can Go Wrong

### Symptom: Token file not being found on second run
**Logs to check:**
- `[YouTube] Token file exists: False` (when it should be True)
- `~/.printstreamer/tokens/youtube_token.json` doesn't exist on host

**Solutions:**
- Ensure run.sh created the tokens directory: `ls -la ~/.printstreamer/`
- Check Docker mount: `docker inspect printstreamer | grep -A10 Mounts`
- Verify file permissions: `ls -la ~/.printstreamer/tokens/`

### Symptom: Token file exists but isn't being read
**Logs to check:**
- `[YouTube] Token file exists: True` (good!)
- `[YouTubeTokenFileDataStore] GetAsync: Token file does not exist` (bad - contradiction!)

**Causes:**
- Race condition with file I/O
- Permission issues when reading the file
- Directory path mismatch between runs

### Symptom: Token file exists but says "null" or empty
**Logs to check:**
- `[YouTubeTokenFileDataStore] GetAsync: Token file is empty`
- `[YouTubeTokenFileDataStore] GetAsync: Failed to parse token: ...`

**Causes:**
- Token write didn't complete before process exited
- File got corrupted
- JSON parsing error

### Symptom: Token loads but authentication still fails
**Logs to check:**
- `[YouTube] Found existing refresh token in store, using it for authentication`
- BUT then no success message appears

**Likely cause:**
- Token is expired or refresh is rejected
- This is expected if the token was revoked or client credentials changed
- Check Google Cloud Console to see if OAuth app is still valid

## File Locations

- **Container**: `/app/data/tokens/youtube_token.json`
- **Host**: `~/.printstreamer/tokens/youtube_token.json`
- **Docker mount**: `-v ~/.printstreamer:/app/data`

## Token File Format

The token is stored as JSON with snake_case fields:
```json
{
  "access_token": "ya29.xxxxx",
  "expires_in": 3600,
  "refresh_token": "1//xxxxx",
  "scope": "https://www.googleapis.com/auth/youtube",
  "token_type": "Bearer"
}
```

The `refresh_token` is the important field for long-term usage.
