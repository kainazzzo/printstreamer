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

# Create bind mount folders if missing
mkdir -p "$REPO_ROOT/timelapse" "$REPO_ROOT/tokens"

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

# Allow interactive auth: enable TTY/STDIN by default when attached to a terminal,
# or when INTERACTIVE=1 is set explicitly.
DOCKER_INTERACTIVE_FLAGS=""
if [[ "${INTERACTIVE:-auto}" == "1" ]]; then
  DOCKER_INTERACTIVE_FLAGS="-it"
elif [[ "${INTERACTIVE:-auto}" == "auto" && -t 0 ]]; then
  DOCKER_INTERACTIVE_FLAGS="-it"
fi

# Host data directory for persistent data (timelapses, audio, tokens)
HOST_DATA_DIR="${HOST_DATA_DIR:-${HOME}/PrintStreamerData}"
mkdir -p "${HOST_DATA_DIR}"
echo "  Data mount  : ${HOST_DATA_DIR} -> /usr/local/share/data"

# Ensure a host token file exists and bind it into the container so OAuth tokens persist
mkdir -p "$REPO_ROOT/tokens"
TOKEN_FILE_HOST="$REPO_ROOT/tokens/youtube_token.json"
if [[ ! -f "$TOKEN_FILE_HOST" ]]; then
  echo '{}' > "$TOKEN_FILE_HOST"
fi
echo "  Token file  : ${TOKEN_FILE_HOST} -> /app/tokens/youtube_token.json (+compat link at /app/youtube_token.json)"
# Only detach if not running interactively
DOCKER_DETACH_FLAG=""
if [[ "${DOCKER_INTERACTIVE_FLAGS}" != *"-i"* && "${DOCKER_INTERACTIVE_FLAGS}" != *"-t"* ]]; then
  DOCKER_DETACH_FLAG="-d"
fi

# Build command array so we can print a safely quoted, copy/pasteable command
DOCKER_CMD=(docker run ${DOCKER_DETACH_FLAG} ${DOCKER_INTERACTIVE_FLAGS} \
  --name "${CONTAINER_NAME}" \
  --restart "${RESTART_POLICY}" \
  -p "${HOST_PORT}:8080" \
  -e "ASPNETCORE_ENVIRONMENT=Home" \
  -v "$REPO_ROOT/appsettings.Home.json:/app/appsettings.Home.json:ro" \
  -v "${HOST_DATA_DIR}:/usr/local/share/data" \
  -v "$REPO_ROOT/tokens:/app/tokens" \
  -v "${TOKEN_FILE_HOST}:/app/youtube_token.json" \
  "${IMAGE_NAME}")

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
