#!/usr/bin/env bash

# docker.sh - Build and run printstreamer in Docker
#
# This script combines build.sh and run.sh functionality.
# It publishes the .NET project, builds the Docker image, and runs the container.

set -Eeuo pipefail
IFS=$'\n\t'

# Locate repo root (one level up from script directory)
SCRIPT_DIR="$(cd -- "$(dirname "${BASH_SOURCE[0]}")" &> /dev/null && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

#############################################
# Configuration
#############################################
DOTNET_IMAGE="${IMAGE_NAME:-printstreamer:latest}"
CONTAINER_NAME="${CONTAINER_NAME:-printstreamer}"
HOST_PORT="${HOST_PORT:-8080}"
PROJECT_FILE="$REPO_ROOT/printstreamer.csproj"
PUBLISH_DIR="$REPO_ROOT/bin/Release/publish"
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

#############################################
# Build Phase
#############################################
echo "=========================================="
echo "PHASE 1: Build"
echo "=========================================="

# Stop and remove any running container
if docker ps -a --format '{{.Names}}' | grep -Eq "^${CONTAINER_NAME}$"; then
  echo "Stopping and removing existing container '$CONTAINER_NAME'..."
  docker stop "$CONTAINER_NAME" || true
  docker rm "$CONTAINER_NAME" || true
fi

# Remove existing image
if docker images --format '{{.Repository}}:{{.Tag}}' | grep -q "^${DOTNET_IMAGE}$"; then
  echo "Removing existing image '$DOTNET_IMAGE'..."
  docker rmi "$DOTNET_IMAGE" || true
fi

echo "Publishing .NET project ($PROJECT_FILE) to $PUBLISH_DIR..."
dotnet publish "$PROJECT_FILE" -c Release -o "$PUBLISH_DIR"

echo "Building Docker image..."
docker build -t "$DOTNET_IMAGE" "$REPO_ROOT"

echo "✓ Docker image '$DOTNET_IMAGE' built successfully."

#############################################
# Run Phase
#############################################
echo
echo "=========================================="
echo "PHASE 2: Run"
echo "=========================================="

echo "Starting printstreamer container..."
echo "  Image       : ${DOTNET_IMAGE}"
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
DOCKER_CMD+=( "${DOTNET_IMAGE}" )

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
