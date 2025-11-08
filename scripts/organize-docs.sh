#!/bin/bash

# Documentation Organizer for Wiki Sync
# This script helps organize documentation files for wiki synchronization

set -e

echo "📋 Organizing documentation for wiki sync..."

# Create docs directory if it doesn't exist
mkdir -p docs

# Copy and organize documentation files
echo "📁 Copying documentation files..."

# Root level docs
for file in *.md; do
    if [[ "$file" != "README.md" ]]; then
        cp "$file" "docs/"
    fi
done

# Feature documentation
if [ -d "features" ]; then
    cp -r features docs/
fi

# Create wiki sidebar if it doesn't exist
if [ ! -f "docs/_Sidebar.md" ]; then
    cat > "docs/_Sidebar.md" << 'EOF'
## Documentation

- [Home](Home)
- [Quick Start](QUICKSTART)
- [Architecture](ARCHITECTURE)
- [API Reference](ENDPOINT_REFERENCE)

## Features

- [Data Flow](DATAFLOW_ARCHITECTURE)
- [Timelapse](timelapse/README)
- [YouTube Integration](YOUTUBE_ARCHITECTURE_ANALYSIS)
- [Audio](audio/README)

## Development

- [Implementation Plan](IMPLEMENTATION_PLAN)
- [Validation](VALIDATION_REPORT)
- [Docker](DOCKER_RELEASE)
EOF
fi

echo "✅ Documentation organized in docs/ directory"
echo "📤 Ready for wiki sync"