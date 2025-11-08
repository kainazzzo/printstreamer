# YouTube Live Broadcast Setup Guide

If you're trying to start a live broadcast but get an error saying **"YouTube OAuth not configured"**, follow these steps:

## Problem
The application cannot create a live YouTube broadcast because:
1. **YouTube OAuth credentials are not configured** (ClientId and ClientSecret are empty)
2. The file `/app/data/tokens/client_secret.json` is empty or doesn't contain valid credentials

## Solution

### Step 1: Create OAuth Credentials in Google Cloud Console

1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Create a new project (or select existing)
3. Enable the **YouTube Data API v3**:
   - Search for "YouTube Data API v3" in the search bar
   - Click "Enable"

4. Create OAuth 2.0 Credentials:
   - Go to **APIs & Services** → **Credentials**
   - Click **Create Credentials** → **OAuth client ID**
   - Choose application type: **Desktop application** (or **Web application** if you want to be more specific)
   - Download the credentials JSON file
   - The file contains:
     - `client_id` (looks like: `xxx.apps.googleusercontent.com`)
     - `client_secret` (a long string)

### Step 2: Configure PrintStreamer

Add the credentials to your configuration. You have two options:

#### Option A: Direct Configuration (appsettings.Home.json)

Edit `appsettings.Home.json` and add:

```json
{
  "YouTube": {
    "OAuth": {
      "ClientId": "your-client-id.apps.googleusercontent.com",
      "ClientSecret": "your-client-secret-here",
      "ClientSecretsFilePath": "/app/data/tokens/client_secret.json"
    },
    "LiveBroadcast": {
      "Enabled": true,
      "Title": "My Printer Stream",
      "Description": "Live stream from my 3D printer",
      "Privacy": "unlisted"
    }
  }
}
```

#### Option B: Using client_secret.json File

1. Copy your downloaded Google OAuth credentials file to:
   - `/app/data/tokens/client_secret.json` (in Docker)
   - Or `/home/you/.printstreamer/client_secret.json` (local development)

2. The file should contain:
```json
{
  "installed": {
    "client_id": "xxx.apps.googleusercontent.com",
    "client_secret": "your-secret",
    "auth_uri": "https://accounts.google.com/o/oauth2/auth",
    "token_uri": "https://oauth2.googleapis.com/token",
    "redirect_uris": ["http://localhost:8080"]
  }
}
```

3. In `appsettings.Home.json`, ensure it points to the file:
```json
{
  "YouTube": {
    "OAuth": {
      "ClientSecretsFilePath": "/app/data/tokens/client_secret.json"
    },
    "LiveBroadcast": {
      "Enabled": true
    }
  }
}
```

### Step 3: Enable Live Broadcast in Config

Make sure this is set to `true` in your config:

```json
{
  "YouTube": {
    "LiveBroadcast": {
      "Enabled": true
    }
  }
}
```

### Step 4: Restart PrintStreamer

After configuring credentials, restart the application:

```bash
# If running with Docker
docker restart printstreamer

# If running locally
dotnet run
```

### Step 5: First-Time OAuth Consent

On first run with valid credentials, if the app is running with a browser available:
- A browser window will open asking for your Google account
- Click "Allow" to authorize PrintStreamer to access YouTube
- The authorization token is automatically saved to `youtube_token.json`

If running headless (no browser):
- Check the logs for an authorization URL
- Open the URL in a browser
- Copy the authorization code
- Either:
  - Set environment variable: `YOUTUBE_OAUTH_CODE=<code>`
  - Or create file: `auth_code.txt` with the code
  - Or add to config: `YouTube:OAuth:AuthCode: <code>`

## Troubleshooting

**Error: "YouTube OAuth not configured"**
- Check that `ClientId` and `ClientSecret` are not empty in your config
- Verify the values are correct (no extra spaces or quotes)

**Error: "YouTube authentication failed"**
- Check that your credentials are valid
- Try deleting `youtube_token.json` to force re-authentication
- Check browser console and application logs for specific errors

**Error: "Failed to create YouTube broadcast"**
- Your account may not have permission to create live broadcasts
- Make sure you're using the account that owns the YouTube channel
- Check YouTube Studio to see if there are any restrictions on your account

**Broadcast stuck in "Starting..."**
- This usually means ffmpeg failed to start or connect
- Check that your printer's webcam URL is accessible
- Look at application logs for ffmpeg errors
- Try using the "Repair" button in the Streaming page

## Security Notes

- **Never commit credentials to version control!**
- Use environment variables or mounted config files for production
- Keep your `client_secret` private
- The `youtube_token.json` file contains credentials - keep it secure
