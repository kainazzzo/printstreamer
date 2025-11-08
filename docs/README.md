# Documentation Management

This repository contains comprehensive documentation that can be automatically synchronized to the GitHub wiki.

## 📁 Documentation Structure

Documentation is organized in several locations:

- **Root directory**: Main project documentation (`README.md`, `QUICKSTART.md`, etc.)
- **`features/`**: Feature-specific documentation
- **`docs/`**: Wiki-specific files and index

## 🔄 Wiki Synchronization

### Automated (GitHub Actions)

A GitHub Actions workflow automatically syncs documentation to the wiki when markdown files are changed:

1. **Enable Wiki**: Repository Settings → General → Features → Wiki (recommended)
2. **Workflow file**: `.github/workflows/wiki-sync.yml` - Already configured
3. **Triggers**: Automatically on pushes to `main` when `.md` files change
4. **Configuration**: Uses `GITHUB_TOKEN` with proper git user configuration
5. **Permissions**: Includes `contents: read` and `pages: write` permissions

### Option 2: Manual Sync

Use the provided script for manual synchronization:

```bash
# From repository root
./scripts/sync-wiki.sh
```


**Requirements:**
- GitHub CLI (`gh`) installed and authenticated
- Push access to the repository

## 📖 Wiki Organization

The wiki uses these special files:

- **`Home.md`**: Wiki homepage (automatically becomes the wiki home)
- **`_Sidebar.md`**: Wiki sidebar navigation (optional)
- **`_Footer.md`**: Wiki footer (optional)

## 🚀 Getting Started

1. **Enable Wiki** (optional): Repository Settings → General → Features → Wiki
2. **Configure Permissions**: The `GITHUB_TOKEN` is automatically available with default permissions
3. **Push Changes**: Commit and push the workflow file to trigger initial sync

### **🚀 Quick Setup**

**No manual token needed!** GitHub Actions provides `GITHUB_TOKEN` automatically.

Just commit and push the workflow files - the wiki will be created and populated automatically on the next push to `main` with markdown changes.

## 📝 Best Practices

- Keep documentation in the repository as the source of truth
- Use relative links within documentation
- The wiki will be read-only (changes there get overwritten)
- Test the sync script locally before relying on automation

## 🔧 Troubleshooting

**Wiki doesn't exist yet:**
- The sync process will create it automatically on first run
- Make sure the wiki feature is enabled in repository settings

**ACTION_MAIL ENV is missing:**
- This error indicates missing git user configuration
- The workflow is now configured with proper `email` and `name` parameters

**Authentication issues:**
- The `GITHUB_TOKEN` is automatically provided by GitHub Actions
- For manual sync, ensure `gh auth login` is completed

**Permission issues:**
- Check repository settings for Actions permissions
- The workflow includes `contents: read` and `pages: write` permissions
- The GITHUB_TOKEN should have write access to the wiki

**Sync fails:**
- Check repository permissions
- Verify the wiki branch exists (created automatically)
- Ensure the `docs/` directory exists and contains markdown files

---

*This documentation is part of the automated wiki sync system.*# Test change for wiki sync
