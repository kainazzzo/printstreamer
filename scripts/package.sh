#!/bin/bash
set -e

ARTIFACT=printstreamer-artifact.tar.gz
BUILD_DIR=bin/Release/publish

echo "Building .NET project..."
dotnet publish -c Release -o $BUILD_DIR

echo "Packaging source, Dockerfile, and release.sh..."
tar --exclude="$ARTIFACT" -czvf $ARTIFACT \
    Dockerfile \
    scripts/release.sh \
    appsettings.json \
    *.csproj \
    *.sln \
    *.md \
    *.yaml \
    *.json \
    scripts/package.sh \
    bin/Release/publish \
    src/ || true

echo "Artifact created: $ARTIFACT"
echo "Copy to your Raspberry Pi, extract, and run: bash release.sh"
