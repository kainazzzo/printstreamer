#!/bin/bash
set -euo pipefail

# Always operate from repo root so relative paths are correct regardless of invocation location
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

DOTNET_IMAGE=printstreamer:latest
CONTAINER_NAME=printstreamer
PROJECT_FILE="$REPO_ROOT/printstreamer.csproj"
PUBLISH_DIR="$REPO_ROOT/bin/Release/publish"

# Stop and remove any running container
if docker ps -a --format '{{.Names}}' | grep -Eq "^$CONTAINER_NAME$"; then
  echo "Stopping and removing existing container '$CONTAINER_NAME'..."
  docker stop "$CONTAINER_NAME" || true
  docker rm "$CONTAINER_NAME" || true
fi

# Remove existing image
if docker images --format '{{.Repository}}:{{.Tag}}' | grep -q "^$DOTNET_IMAGE$"; then
  echo "Removing existing image '$DOTNET_IMAGE'..."
  docker rmi "$DOTNET_IMAGE" || true
fi

echo "Publishing .NET project ($PROJECT_FILE) to $PUBLISH_DIR..."
dotnet publish "$PROJECT_FILE" -c Release -o "$PUBLISH_DIR"

echo "Building Docker image..."
docker build -t "$DOTNET_IMAGE" "$REPO_ROOT"

echo "Docker image '$DOTNET_IMAGE' built and available locally."
echo "Run with: docker run --rm -it --name $CONTAINER_NAME -v $(pwd)/appsettings.json:/app/appsettings.json $DOTNET_IMAGE"
