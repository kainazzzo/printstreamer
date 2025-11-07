#!/usr/bin/env bash

# run.sh - Run printstreamer in Docker using appsettings.Home.json
#
# This script uses ASP.NET Core's environment-specific configuration.
# Create appsettings.Home.json in the project root with your settings.

set -Eeuo pipefail
IFS=$'\n\t'

# Locate repo root (one level up from script directory)
SCRIPT_DIR="$(cd -- "$(dirname "${BASH_SOURCE[0]}")" &> /dev/null && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

#############################################
# Configuration
#############################################
CONTAINER_NAME="${CONTAINER_NAME:-printstreamer}"
HOST_PORT="${HOST_PORT:-8080}"
IMAGE_NAME="${IMAGE_NAME:-printstreamer:latest}"
RESTART_POLICY="${RESTART_POLICY:-unless-stopped}"

#############################################
# Preconditions
#############################################
if ! command -v docker &>/dev/null; then
  echo "Error: docker is not installed or not on PATH" >&2
  exit 1
fi

if [[ ! -f "$REPO_ROOT/appsettings.Home.json" ]]; then
  echo "Error: appsettings.Home.json not found in $REPO_ROOT" >&2
  echo "Tip: Copy appsettings.Home.json.example to appsettings.Home.json and edit with your settings." >&2
  exit 1
fi

# Build the image if it doesn't exist locally
if ! docker image inspect "$IMAGE_NAME" >/dev/null 2>&1; then
  echo "Docker image '$IMAGE_NAME' not found. Building from Dockerfile..."
  docker build -t "$IMAGE_NAME" "$REPO_ROOT"
fi

# Stop and remove existing container if running
if docker ps -a --format '{{.Names}}' | grep -q "^${CONTAINER_NAME}$"; then
  echo "Stopping and removing existing container: ${CONTAINER_NAME}"
  docker stop "${CONTAINER_NAME}" >/dev/null 2>&1 || true
  docker rm "${CONTAINER_NAME}" >/dev/null 2>&1 || true
fi

echo "Starting printstreamer container..."
echo "  Image       : ${IMAGE_NAME}"
echo "  Container   : ${CONTAINER_NAME}"
echo "  Port        : ${HOST_PORT} -> 8080"
echo "  Environment : Home"
echo "  Config      : appsettings.Home.json"
echo "  Data mount  : ~/.printstreamer"

# Allow interactive auth: enable TTY/STDIN when INTERACTIVE=1 is set explicitly,
# or when INTERACTIVE=auto and attached to a terminal.
DOCKER_INTERACTIVE_FLAGS=""
if [[ "${INTERACTIVE:-0}" == "1" ]]; then
  DOCKER_INTERACTIVE_FLAGS="-it"
elif [[ "${INTERACTIVE:-0}" == "auto" && -t 0 ]]; then
  DOCKER_INTERACTIVE_FLAGS="-it"
fi

# Ensure ~/.printstreamer directory structure exists
PRINTSTREAMER_HOME="$HOME/.printstreamer"
TOKENS_DIR="$PRINTSTREAMER_HOME/tokens"

# Create tokens directory early so Docker can mount it
mkdir -p "$TOKENS_DIR"

# Broadcast reuse store (caches broadcast IDs to avoid creating duplicates)
BROADCAST_STORE_HOST="$PRINTSTREAMER_HOME/youtube_reuse_store.json"
if [[ ! -f "$BROADCAST_STORE_HOST" ]]; then
  echo '[]' > "$BROADCAST_STORE_HOST"
fi

echo "  Broadcast store : ${BROADCAST_STORE_HOST}"
echo "  Tokens dir      : ${TOKENS_DIR}"

# Only detach if not running interactively
DOCKER_DETACH_FLAG=""
if [[ "${DOCKER_INTERACTIVE_FLAGS}" != *"-i"* && "${DOCKER_INTERACTIVE_FLAGS}" != *"-t"* ]]; then
  DOCKER_DETACH_FLAG="-d"
fi

# Build command array so we can print a safely quoted, copy/pasteable command
# Base docker run args
DOCKER_CMD=(docker run ${DOCKER_DETACH_FLAG} ${DOCKER_INTERACTIVE_FLAGS} \
  --name "${CONTAINER_NAME}" \
  --restart "${RESTART_POLICY}" \
  --label "environment=Home" \
  -p "${HOST_PORT}:8080" \
  -e "ASPNETCORE_ENVIRONMENT=Home" \
  -v "$REPO_ROOT/appsettings.Home.json:/app/appsettings.Home.json:ro" \
  -v "$PRINTSTREAMER_HOME:/app/data")

# Forward optional one-time OAuth code for non-interactive auth
if [[ -n "${YOUTUBE_OAUTH_CODE:-}" ]]; then
  DOCKER_CMD+=( -e "YOUTUBE_OAUTH_CODE=${YOUTUBE_OAUTH_CODE}" )
fi

# Allow overriding token file location if desired (maps to config key YouTube:OAuth:TokenFile)
if [[ -n "${YOUTUBE_OAUTH_TOKEN_FILE:-}" ]]; then
  DOCKER_CMD+=( -e "YouTube__OAuth__TokenFile=${YOUTUBE_OAUTH_TOKEN_FILE}" )
fi

# Image name is last
DOCKER_CMD+=( "${IMAGE_NAME}" )

# Print the command in a shell-quoted form so it's obvious what will run
printf "docker run command:"
for arg in "${DOCKER_CMD[@]}"; do
  printf ' %q' "$arg"
done
printf "\n\n"

# Execute the built command
"${DOCKER_CMD[@]}"

echo
echo "✓ Container started. Access: http://localhost:${HOST_PORT}"
echo "View logs: docker logs -f ${CONTAINER_NAME}"
echo "Stop: docker stop ${CONTAINER_NAME}"
