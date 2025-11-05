# YouTube / Google Cloud: creating a project, OAuth client and adding credentials to PrintStreamer

This document walks through the exact steps you should perform in the Google Cloud Console / Google APIs pages to:

- create a new Google Cloud project
- configure the OAuth consent screen (required for YouTube scopes)
- create an OAuth Client ID (desktop/web) and get the Client ID + Client Secret
- enable the YouTube APIs needed for stream management
- add client credentials to PrintStreamer's configuration (`appsettings.json` / `appsettings.*.json`)
- obtain and (optionally) seed a refresh token for headless operation

The guidance below matches how `Services/YouTubeService.cs` uses OAuth and tokens (it expects config keys `YouTube:OAuth:ClientId`, `YouTube:OAuth:ClientSecret`, optional `YouTube:OAuth:RefreshToken`, and reads/writes `youtube_token.json`).

## Quick notes from the code

- The app requests the scope: `Google.Apis.YouTube.v3.YouTubeService.Scope.Youtube` — this is the full YouTube scope (full account access for YouTube API / live streaming).
- Tokens are persisted (by the library wrapper in this repo) to `youtube_token.json` in the working directory. The project also supports seeding a refresh token via `YouTube:OAuth:RefreshToken` in configuration for headless runs.
- The code supports interactive browser flows (desktop/loopback) and a manual fallback that uses the `urn:ietf:wg:oauth:2.0:oob` style flow.

IMPORTANT: Google has deprecated OOB flows. Prefer creating a "Desktop" OAuth client or a "Web application" client configured to use loopback (http://127.0.0.1) redirects. The code's automatic flow uses your system browser and will work with a Desktop OAuth client.

## Scopes (exact strings and examples)

The code in this repo requests the single, full-access YouTube scope defined as:

- `https://www.googleapis.com/auth/youtube`

This scope grants broad access to manage YouTube resources (including creating/managing live broadcasts and streams). It is the same scope referenced by `Google.Apis.YouTube.v3.YouTubeService.Scope.Youtube` in the code.

Other related scopes you may encounter or choose instead for narrower access:

- `https://www.googleapis.com/auth/youtube.readonly` — read-only access to YouTube account data (cannot manage streams).
- `https://www.googleapis.com/auth/youtube.upload` — permission to upload videos (may not be sufficient alone for live stream management).
- `https://www.googleapis.com/auth/youtube.force-ssl` — similar to full access but enforces SSL; used by some clients.

If you need to request a refresh token for headless operation, make sure your interactive authorization includes these query parameters (when constructing the auth URL):

- `access_type=offline` — requests a refresh token from Google.
- `prompt=consent` — forces Google to show the consent screen and issue a refresh token even if the user previously consented.

Example manual authorization URL (replace CLIENT_ID and redirect URI as appropriate).

https://accounts.google.com/o/oauth2/v2/auth?client_id=CLIENT_ID&response_type=code&scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fyoutube&access_type=offline&prompt=consent&redirect_uri=REDIRECT_URI

Notes:

- URL-encode the scope and redirect URI. The example above encodes `https://www.googleapis.com/auth/youtube` as `https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fyoutube`.
- The `access_type=offline&prompt=consent` pair is the recommended way to ensure Google returns a refresh token that you can seed into `YouTube:OAuth:RefreshToken`.
- For most uses of this project (live streaming management) the single full `youtube` scope is adequate and simplest.

---

## 1) Create a new Google Cloud project

1. Sign in: https://console.cloud.google.com with the Google account that owns the YouTube channel you intend to stream from.
2. In the top-left project selector click on the current project name and choose "NEW PROJECT".
3. Enter a project name (e.g. `printstreamer-<yourname>`). Leave organization blank if you don't have one. Click "Create".
4. Wait for the project creation to complete and ensure the project is selected (top-left).

Record the project name / project ID — you'll need it for the Cloud Console navigation and to enable APIs.

## 2) Enable the YouTube APIs the app needs

1. In the Cloud Console left-hand menu go to `APIs & Services > Library`.
2. Search for and enable these APIs (enable each one in the context of the project you created):
   - "YouTube Data API v3" (primary API for creating broadcasts/streams and general YouTube data)
   - (optional) "YouTube Live Streaming API" (if you see it listed separately; functionality for managing broadcasts/streams is provided by the Data API but some consoles may list Live Streaming related API entries)
3. After enabling, go to `APIs & Services > Dashboard` to confirm they are enabled for the project.

Notes: enabling APIs should be sufficient; YouTube APIs don't require billing for basic usage, but quotas apply. If you expect high-volume usage, review quotas and request increases in `APIs & Services > Quotas`.

## 3) Configure the OAuth consent screen (required for YouTube scopes)

Because the app will request sensitive/private YouTube scopes, you must configure an OAuth consent screen before creating OAuth credentials.

1. In Cloud Console go to `APIs & Services > OAuth consent screen`.
2. Choose "External" for the user type (most developers) and click "Create".
3. Fill out the fields: App name, User support email, Developer contact email.
4. Under "Scopes", you don't need to add scopes here manually, but be aware the app will request the full YouTube scope (sensitive). Add the scope explanation if desired.
5. If your app will be for personal/dev only (not distributed publicly), add your Google account as a "Test user" under "Test users" and save.

Important: If you plan to publish the app to external users you'll need to go through Google's verification process for sensitive scopes. For development / one-account use, keeping your account as a test user is sufficient.

## 4) Create OAuth credentials (Client ID & Client Secret)

You have two common choices. Both will work, but pick based on how you run PrintStreamer:

- Desktop app (recommended for local dev / CLI use): Choose "Desktop app" — the Google .NET library will open a system browser and use a loopback redirect. No manual redirect URIs required.
- Web application: Choose "Web application" and add a redirect URI pointing at a loopback address your app will listen on (for example `http://127.0.0.1:PORT/`). If you use this, add the exact redirect URI that the app uses. (If you don't know the port the library will pick one for you.)

Steps:
1. Go to `APIs & Services > Credentials`.
2. Click `Create credentials > OAuth client ID`.
3. Select `Application type`:
   - For local/CLI: pick `Desktop app`.
   - For server/web: pick `Web application` and provide the redirect URIs you intend to use.
4. Give it a name (e.g. `PrintStreamer Desktop Client`) and click `Create`.
5. Copy the resulting **Client ID** and **Client secret**. You can also click the download icon to download the JSON file containing these keys.

Notes about redirect URIs and OOB:
- The code includes a manual fallback that prints an authorization URL using `urn:ietf:wg:oauth:2.0:oob`. Google deprecated OOB flows in 2022, so prefer Desktop or loopback web redirect. The Google .NET client library will handle the loopback redirect if you use a Desktop client.
- If you must use a web client and a manual flow, ensure the redirect URI you use matches the OAuth client's registered redirect URIs.

## 5) Add ClientId and ClientSecret to PrintStreamer's configuration

Open your `appsettings.json` (or `appsettings.Local.json` / `appsettings.Home.json`) and add the YouTube OAuth block. Example minimal snippet:

```json
"YouTube": {
  "OAuth": {
    "ClientId": "YOUR_CLIENT_ID.apps.googleusercontent.com",
    "ClientSecret": "YOUR_CLIENT_SECRET",
    "RefreshToken": ""  // optional: seed a refresh token for headless runs
  },
  "LiveBroadcast": {
    "Title": "Print Streamer Live",
    "Description": "Live stream from my 3D printer",
    "Privacy": "unlisted",
    "CategoryId": "28"
  },
  "LiveStream": {
    "Title": "Print Stream",
    "Description": "3D printer camera feed"
  }
}
```

- The code reads `YouTube:OAuth:ClientId`, `YouTube:OAuth:ClientSecret`, and an optional `YouTube:OAuth:RefreshToken`.
- `youtube_token.json` is used by the app at runtime to persist the OAuth token response (access_token + refresh_token). See next section on seeding a refresh token for unattended operation.

## 6) Performing the initial interactive authorization (to create `youtube_token.json`)

If running locally and the machine has a browser, the code will attempt to open the system browser and complete the OAuth flow automatically. Steps:

1. Start PrintStreamer (or run the YouTube/auth-related command that calls `AuthenticateAsync`).
2. The app should open your browser to a Google account consent screen. Sign in with the account that owns the YouTube channel and accept the requested scopes.
3. After consent completes the token response (including a refresh token) will be written to `youtube_token.json` in the working directory.

If auth fails due to consent screen/test-user restrictions, revisit the OAuth consent screen configuration and add your account as a test user.

## 7) How to extract a refresh token (seed headless operation)

If you want PrintStreamer to operate headless (no interactive login), you can seed a refresh token into the config. Two common ways to obtain that refresh token:

A) Interactive run + copy from `youtube_token.json` (recommended)
- Run the app once interactively and let it produce `youtube_token.json`.
- Open `youtube_token.json` and copy the `refresh_token` value.
- Put that value into `appsettings.json` at `YouTube:OAuth:RefreshToken`.
- The app's code will seed that refresh token into the token store on startup and will not require an interactive login.

B) Use OAuth tools (less convenient)
- Use the OAuth 2.0 Playground or custom auth URL with `access_type=offline&prompt=consent` to force a refresh token. Ensure you use the *same* client id/secret. Copy the refresh_token from the response and store it in `appsettings`.

Notes and caveats when seeding a refresh token:
- Google will only issue a refresh token the first time the user consents (unless you include `prompt=consent`). If you reconsent with the same account and do not pass `prompt=consent` you may not receive a refresh token.
- If the refresh token is revoked or was issued to a different OAuth client, refresh attempts will fail with `unauthorized_client` or `invalid_grant`.
- Be careful storing refresh tokens in source-controlled files. If you keep `appsettings.json` in git, use `appsettings.Local.json` or environment variables to keep secrets out of VCS.

## 8) `youtube_token.json` format (what the repo stores)

The repository's token store writes a small JSON file with snake_case fields. A sample `youtube_token.json` produced by the library looks like:

```json
{
  "access_token": "ya29.a0Af...",
  "expires_in": 3599,
  "refresh_token": "1//0g...",
  "scope": "https://www.googleapis.com/auth/youtube",
  "token_type": "Bearer"
}
```

If you seed a `RefreshToken` value in `appsettings`, the code will write it into `youtube_token.json` on startup so the same format will be used.

## 9) Make sure the YouTube channel has live streaming enabled

Creating broadcasts and streams via API requires the account's YouTube channel to have live streaming enabled. To enable live streaming on the account:

1. Sign into the same Google account in https://studio.youtube.com.
2. Click `Create` (camera icon) -> `Go Live` and follow the prompts to verify your account (phone verification may be required).
3. Note: verification and enabling live streaming may take up to 24 hours in some cases.

If you try to create a broadcast and get errors such as `liveStreamingNotEnabled` or similar, check the channel's live streaming status in YouTube Studio.

## 10) Common errors and troubleshooting

- unauthorized_client or invalid_client
  - Cause: the client id/secret don't match the refresh token, or the token was issued to a different OAuth client.
  - Fix: re-run the interactive flow with the correct client id/secret and obtain a fresh refresh token.

- access_denied / consent required / not a test user
  - Cause: OAuth consent screen is still in development and your account is not listed as a test user.
  - Fix: add your account as a test user in `APIs & Services > OAuth consent screen` or verify the app with Google.

- liveStreamingNotEnabled / 403
  - Cause: the YouTube channel is not enabled for live streaming or needs verification.
  - Fix: enable live streaming in YouTube Studio and complete phone verification.

- quotaExceeded
  - Cause: your project reached API quotas.
  - Fix: check `APIs & Services > Quotas` and request more quota if appropriate.

- unauthorized_client when refreshing a seeded token
  - Cause: refresh token was issued to a different client or was revoked.
  - Fix: remove seeded refresh token and perform interactive auth so the library can store a token for the correct client.

## 11) Security guidance

- Do not commit Client Secret or Refresh Token values to public repositories. Use `appsettings.Local.json`, environment variables, or a secret manager.
- If you must keep credentials in the project for automation, ensure the repository is private and rotate credentials if leaked.

## 12) Summary checklist (copy-paste)

- [ ] Create a Google Cloud project
- [ ] Enable `YouTube Data API v3` (and Live Streaming API if listed)
- [ ] Configure OAuth consent screen (add your account as test user for dev)
- [ ] Create OAuth client (prefer `Desktop app` for local/CLI runs)
- [ ] Copy `ClientId` and `ClientSecret` into `appsettings.*.json` under `YouTube:OAuth`
- [ ] Start the app and complete the interactive consent to produce `youtube_token.json`
- [ ] (Optional) Copy `refresh_token` to `YouTube:OAuth:RefreshToken` for headless runs
- [ ] Ensure YouTube channel has live streaming enabled in YouTube Studio

---

If you want, I can:
- generate a sample `appsettings.Local.json` with placeholders (keeps secrets out of commit), or
- add a small note to `README.md` linking to this doc, or
- create a small helper script that prints the interactive auth URL with the proper query params (`access_type=offline&prompt=consent`) if you prefer to obtain a refresh token via a manual browser.

Which of those would you like next?