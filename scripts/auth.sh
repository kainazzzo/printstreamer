#!/usr/bin/env bash

# auth.sh - Obtain a YouTube OAuth token via installed-app or device flow
#
# The script looks for client_secret.json inside ~/.printstreamer/tokens (or the
# directory specified via PRINTSTREAMER_HOME / TOKENS_DIR / CLIENT_SECRET_FILE)
# and stores the resulting youtube_token.json next to it. The token file matches
# the format that PrintStreamer expects (snake_case fields).

set -Eeuo pipefail
IFS=$'\n\t'

usage() {
  cat <<'EOF'
Usage: scripts/auth.sh [--help]

Steps performed:
  1. Read client_id/client_secret from ~/.printstreamer/tokens/client_secret.json
     (override with CLIENT_SECRET_FILE if needed).
  2. Run an interactive OAuth flow (installed/desktop by default) and prompt
     you to grant access in the browser.
  3. Exchange the returned authorization code for access + refresh tokens.
  4. Write youtube_token.json into ~/.printstreamer/tokens (override with
     YOUTUBE_TOKEN_FILE).

Environment variables:
  PRINTSTREAMER_HOME   Override ~/.printstreamer root
  TOKENS_DIR           Override tokens directory (default: $PRINTSTREAMER_HOME/tokens)
  CLIENT_SECRET_FILE   Override path to client_secret.json
  YOUTUBE_TOKEN_FILE   Override output token path (default: $TOKENS_DIR/youtube_token.json)
  YOUTUBE_SCOPE        Override OAuth scope (default: https://www.googleapis.com/auth/youtube)
  GOOGLE_AUTH_FLOW     Choose 'installed' (default), 'device', or 'auto'
  OAUTH_REDIRECT_URI   Redirect URI for installed flow (default: urn:ietf:wg:oauth:2.0:oob)
  OPEN_AUTH_URL        If set to 1, attempt to open the auth URL automatically

Dependencies: curl, jq, (optional) xdg-open
EOF
}

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  usage
  exit 0
fi

require_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Error: missing required command '$1'" >&2
    exit 1
  fi
}

require_cmd curl
require_cmd jq

PRINTSTREAMER_HOME=${PRINTSTREAMER_HOME:-"$HOME/.printstreamer"}
TOKENS_DIR=${TOKENS_DIR:-"$PRINTSTREAMER_HOME/tokens"}
CLIENT_SECRET_FILE=${CLIENT_SECRET_FILE:-"$TOKENS_DIR/client_secret.json"}
YOUTUBE_TOKEN_FILE=${YOUTUBE_TOKEN_FILE:-"$TOKENS_DIR/youtube_token.json"}
YOUTUBE_SCOPE=${YOUTUBE_SCOPE:-"https://www.googleapis.com/auth/youtube"}
GOOGLE_DEVICE_ENDPOINT=${GOOGLE_DEVICE_ENDPOINT:-"https://oauth2.googleapis.com/device/code"}
GOOGLE_TOKEN_ENDPOINT=${GOOGLE_TOKEN_ENDPOINT:-"https://oauth2.googleapis.com/token"}
GOOGLE_AUTH_URL=${GOOGLE_AUTH_URL:-"https://accounts.google.com/o/oauth2/v2/auth"}
OAUTH_REDIRECT_URI=${OAUTH_REDIRECT_URI:-"urn:ietf:wg:oauth:2.0:oob"}
GOOGLE_AUTH_FLOW=${GOOGLE_AUTH_FLOW:-"installed"}
OPEN_AUTH_URL=${OPEN_AUTH_URL:-"auto"}

mkdir -p "$TOKENS_DIR"

if [[ ! -f "$CLIENT_SECRET_FILE" ]]; then
  echo "Error: client secret file not found at $CLIENT_SECRET_FILE" >&2
  echo "Tip: download the OAuth client JSON from Google Cloud Console and place it there." >&2
  exit 1
fi

CLIENT_ID=$(jq -e -r '.installed.client_id // .web.client_id // .client_id' "$CLIENT_SECRET_FILE")
CLIENT_SECRET=$(jq -e -r '.installed.client_secret // .web.client_secret // .client_secret' "$CLIENT_SECRET_FILE")

if [[ -z "$CLIENT_ID" || -z "$CLIENT_SECRET" ]]; then
  echo "Error: unable to read client_id/client_secret from $CLIENT_SECRET_FILE" >&2
  exit 1
fi
# shellcheck disable=SC2016
urlencode() {
  local str="$1" out="" i c
  for ((i = 0; i < ${#str}; i++)); do
    c=${str:i:1}
    case "$c" in
      [a-zA-Z0-9._~-]) out+="$c" ;;
      ' ') out+='%20' ;;
      *) printf -v hex '%%%02X' "'$c"; out+="$hex" ;;
    esac
  done
  printf '%s' "$out"
}

run_device_flow() {
  local device_payload device_error DEVICE_CODE USER_CODE VERIFICATION_URL EXPIRES_IN INTERVAL
  device_payload=$(curl -sS -X POST "$GOOGLE_DEVICE_ENDPOINT" \
    -d "client_id=$CLIENT_ID" \
    -d "scope=$YOUTUBE_SCOPE")

  device_error=$(echo "$device_payload" | jq -r '.error // empty')
  if [[ -n "$device_error" ]]; then
    echo "Error: device authorization request failed ($device_error)" >&2
    echo "$device_payload" >&2
    return 1
  fi

  DEVICE_CODE=$(echo "$device_payload" | jq -r '.device_code')
  USER_CODE=$(echo "$device_payload" | jq -r '.user_code')
  VERIFICATION_URL=$(echo "$device_payload" | jq -r '.verification_url // .verification_uri')
  EXPIRES_IN=$(echo "$device_payload" | jq -r '.expires_in')
  INTERVAL=$(echo "$device_payload" | jq -r '.interval // 5')

  local -i deadline
  deadline=$(($(date +%s) + EXPIRES_IN))

  cat <<EOF
========================================
Google OAuth Device Authorization
========================================
1. In a browser, open: $VERIFICATION_URL
2. When prompted, enter the code: $USER_CODE
3. Approve the requested YouTube access for your channel/account.

This code expires in $EXPIRES_IN seconds. Leave this terminal window open; the
script will poll Google's token endpoint until authorization completes.
EOF

  local token_json="" current_interval=$INTERVAL token_response error_code
  local -i now
  while true; do
    now=$(date +%s)
    if (( now >= deadline )); then
      echo "Error: device code expired before authorization completed." >&2
      return 1
    fi

    sleep "$current_interval"

    token_response=$(curl -sS -X POST "$GOOGLE_TOKEN_ENDPOINT" \
      -d "client_id=$CLIENT_ID" \
      -d "client_secret=$CLIENT_SECRET" \
      -d "device_code=$DEVICE_CODE" \
      -d 'grant_type=urn:ietf:params:oauth:grant-type:device_code')

    if echo "$token_response" | jq -e '.access_token? // empty' >/dev/null; then
      token_json=$(echo "$token_response" | jq '{access_token, expires_in, refresh_token, scope, token_type}')
      printf '%s' "$token_json"
      return 0
    fi

    error_code=$(echo "$token_response" | jq -r '.error // empty')

    case "$error_code" in
      authorization_pending)
        continue
        ;;
      slow_down)
        current_interval=$((current_interval + 5))
        continue
        ;;
      access_denied)
        echo "Error: access denied. Restart the flow if this was accidental." >&2
        return 1
        ;;
      expired_token)
        echo "Error: device code expired. Restart the flow." >&2
        return 1
        ;;
      *)
        echo "Error: unexpected response from token endpoint:" >&2
        echo "$token_response" >&2
        return 1
        ;;
    esac
  done
}

run_installed_flow() {
  local encoded_scope encoded_redirect auth_query auth_url browser_help token_response token_error auth_code trimmed_code
  encoded_scope=$(urlencode "$YOUTUBE_SCOPE")
  encoded_redirect=$(urlencode "$OAUTH_REDIRECT_URI")
  auth_query="client_id=$(urlencode "$CLIENT_ID")&redirect_uri=$encoded_redirect&response_type=code&scope=$encoded_scope&access_type=offline&prompt=consent"
  auth_url="$GOOGLE_AUTH_URL?$auth_query"

  cat <<EOF
========================================
Google OAuth Installed-App Flow
========================================
1. Open the authorization URL below in a browser (or let the script try).
2. Sign in with the YouTube channel account and grant the requested scope.
3. Copy the authorization code Google displays and paste it back here.

$auth_url
EOF

  if [[ "$OPEN_AUTH_URL" == "1" ]] || { [[ "$OPEN_AUTH_URL" == "auto" ]] && command -v xdg-open >/dev/null 2>&1; }; then
    xdg-open "$auth_url" >/dev/null 2>&1 || true
  fi

  read -rp $'\nPaste authorization code: ' auth_code
  trimmed_code=$(echo "$auth_code" | tr -d '\r\n')
  if [[ -z "$trimmed_code" ]]; then
    echo "Error: no authorization code provided." >&2
    return 1
  fi

  token_response=$(curl -sS -X POST "$GOOGLE_TOKEN_ENDPOINT" \
    -d "client_id=$CLIENT_ID" \
    -d "client_secret=$CLIENT_SECRET" \
    -d "code=$trimmed_code" \
    -d "redirect_uri=$OAUTH_REDIRECT_URI" \
    -d 'grant_type=authorization_code')

  token_error=$(echo "$token_response" | jq -r '.error // empty')
  if [[ -n "$token_error" ]]; then
    echo "Error: token exchange failed ($token_error)." >&2
    echo "$token_response" >&2
    return 1
  fi

  echo "$token_response" | jq '{access_token, expires_in, refresh_token, scope, token_type}'
}

token_json=""
flow_choice=$(echo "$GOOGLE_AUTH_FLOW" | tr '[:upper:]' '[:lower:]')

case "$flow_choice" in
  device)
    if ! token_json=$(run_device_flow); then
      exit 1
    fi
    ;;
  installed|auto|"")
    if token_json=$(run_installed_flow); then
      :
    elif [[ "$flow_choice" == "auto" ]]; then
      echo "Installed-app flow failed; attempting device code flow..." >&2
      if ! token_json=$(run_device_flow); then
        exit 1
      fi
    else
      exit 1
    fi
    ;;
  *)
    echo "Error: unsupported GOOGLE_AUTH_FLOW '$GOOGLE_AUTH_FLOW' (use installed, device, or auto)." >&2
    exit 1
    ;;
esac

if [[ -z "$token_json" ]]; then
  echo "Error: failed to capture token response." >&2
  exit 1
fi

if [[ $(echo "$token_json" | jq -r '.refresh_token // empty') == "" ]]; then
  echo "Warning: Google did not return a refresh_token. Rerun the script with prompt=consent by revoking access in your Google Account security settings." >&2
fi

tmp_file=$(mktemp "$YOUTUBE_TOKEN_FILE.XXXXXX")
chmod 600 "$tmp_file"
printf '%s\n' "$token_json" > "$tmp_file"
mv "$tmp_file" "$YOUTUBE_TOKEN_FILE"

cat <<EOF

Success! Saved tokens to: $YOUTUBE_TOKEN_FILE
Make sure your appsettings/App config points YouTube:OAuth:TokenFile at this path.
EOF
