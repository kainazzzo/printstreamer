# Documentation Management

This repository contains comprehensive documentation that can be automatically synchronized to the GitHub wiki.

## 📁 Documentation Structure

Documentation is organized in several locations:

- **Root directory**: Main project documentation (`README.md`, `QUICKSTART.md`, etc.)
- **`features/`**: Feature-specific documentation
- **`docs/`**: Wiki-specific files and index

## 🔄 Wiki Synchronization

### Option 1: Automated (Recommended)

A GitHub Actions workflow automatically syncs documentation to the wiki when markdown files are changed:

1. **Setup**: The workflow file `.github/workflows/wiki-sync.yml` is already created
2. **Trigger**: Automatically runs on pushes to `main` branch when `.md` files change
3. **Requirements**: Add `GH_TOKEN` secret to your repository settings

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

**Authentication issues:**
- The `GITHUB_TOKEN` is automatically provided by GitHub Actions
- For manual sync, ensure `gh auth login` is completed

**Permission issues:**
- Check repository settings for Actions permissions
- The GITHUB_TOKEN should have write access to the wiki

**Sync fails:**
- Check repository permissions
- Verify the wiki branch exists (created automatically)

---

*This documentation is part of the automated wiki sync system.*