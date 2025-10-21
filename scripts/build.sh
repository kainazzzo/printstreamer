#!/bin/bash
set -e

DOTNET_IMAGE=printstreamer:latest
CONTAINER_NAME=printstreamer

# Stop and remove any running container
if docker ps -a --format '{{.Names}}' | grep -Eq "^$CONTAINER_NAME$"; then
  echo "Stopping and removing existing container '$CONTAINER_NAME'..."
  docker stop $CONTAINER_NAME || true
  docker rm $CONTAINER_NAME || true
fi

# Remove existing image
if docker images --format '{{.Repository}}:{{.Tag}}' | grep -q "^$DOTNET_IMAGE$"; then
  echo "Removing existing image '$DOTNET_IMAGE'..."
  docker rmi $DOTNET_IMAGE || true
fi

echo "Building .NET project..."
dotnet publish -c Release -o ./bin/Release/publish

echo "Building Docker image..."
docker build -t $DOTNET_IMAGE .

echo "Docker image '$DOTNET_IMAGE' built and available locally."
echo "Run with: docker run --rm -it --name $CONTAINER_NAME -v $(pwd)/appsettings.json:/app/appsettings.json $DOTNET_IMAGE"
