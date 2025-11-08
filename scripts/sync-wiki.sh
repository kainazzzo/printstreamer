#!/bin/bash

# PrintStreamer Wiki Sync Script
# This script syncs documentation from the repo to the GitHub wiki

set -e

echo "🔄 Syncing documentation to GitHub Wiki..."

# Check if we're in the right directory
if [ ! -f "printstreamer.csproj" ]; then
    echo "❌ Error: Run this script from the repository root"
    exit 1
fi

# Check if GitHub CLI is installed
if ! command -v gh &> /dev/null; then
    echo "❌ Error: GitHub CLI (gh) is required. Install from https://cli.github.com/"
    exit 1
fi

# Get repository info
REPO_URL=$(gh repo view --json url -q .url)
REPO_NAME=$(basename "$REPO_URL" .git)
WIKI_URL="${REPO_URL}.wiki.git"

echo "📁 Repository: $REPO_NAME"
echo "🔗 Wiki URL: $WIKI_URL"

# Create temp directory for wiki
TEMP_DIR=$(mktemp -d)
echo "📂 Working in: $TEMP_DIR"

# Clone wiki repository
echo "📥 Cloning wiki repository..."
if ! git clone "$WIKI_URL" "$TEMP_DIR/wiki" 2>/dev/null; then
    echo "⚠️  Wiki repository doesn't exist yet. It will be created automatically."
    mkdir -p "$TEMP_DIR/wiki"
    cd "$TEMP_DIR/wiki"
    git init
    git remote add origin "$WIKI_URL"
fi

cd "$TEMP_DIR/wiki"

# Copy documentation files
echo "📋 Copying documentation files..."

# Copy root documentation files
find "../../.." -maxdepth 1 -name "*.md" -exec cp {} . \;

# Copy feature documentation
if [ -d "../../../features" ]; then
    mkdir -p features
    cp -r "../../../features/"* features/ 2>/dev/null || true
fi

# Copy docs directory if it exists
if [ -d "../../../docs" ]; then
    cp -r "../../../docs/"* . 2>/dev/null || true
fi

# Rename Home.md to Home.md (GitHub wiki expects this)
if [ -f "Home.md" ]; then
    mv Home.md Home.md
fi

# Add and commit changes
echo "💾 Committing changes..."
git add .
if git diff --cached --quiet; then
    echo "✅ No changes to commit"
else
    git commit -m "Sync documentation from main repository

Automated sync from $(date)
Repository: $REPO_URL"
    echo "📤 Pushing to wiki..."
    git push origin main 2>/dev/null || git push origin master
    echo "✅ Wiki updated successfully!"
fi

# Cleanup
cd /
rm -rf "$TEMP_DIR"

echo "🎉 Documentation sync complete!"
echo "📖 View your wiki at: https://github.com/$(gh repo view --json owner.login,name -q '.owner.login + \"/\" + .name')/wiki"