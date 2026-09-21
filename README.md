<div align="center">
  <img src="icon.jpg" width="150" alt="JellyFetch Plugin Logo">
  
  # JellyFetch — Jellyfin Downloads Plugin
  
  **Automated media scraping, cloud downloading, and library injection for Jellyfin.**
  
  [![Version](https://img.shields.io/badge/version-1.0.3-blue)](https://github.com/Mohdsuhailpgdi/JellyFetch/releases/latest)
  [![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9.x--12.x-orange)](https://jellyfin.org)
  [![Platform](https://img.shields.io/badge/platform-Linux%20%7C%20Docker-lightgrey)](https://github.com/Mohdsuhailpgdi/JellyFetch)
</div>

---

## 🇮🇳 Built for Regional Audiences
This plugin is specifically optimized for Indian media consumers. The default scraping engine is deeply integrated with **1tamilmv**, providing robust, automated tracking and downloading of regional content, with extensive fallback capabilities.

---

## ✨ What's New in v1.0.3

- **🔧 Fixed: Scraper Crash (`MissingMethodException`)** — Resolved interface signature differences in `ILibraryManager.GetItemList` across Jellyfin 10.9, 10.10, and 12.1+ so manual and automated scraping runs without crashing.
- **🌐 Multi-Language Movie Merging** — When a movie has releases in multiple languages (e.g. Tamil and Malayalam releases of the same title), the scraper atomically merges all magnet options under the single movie entry instead of overwriting or creating duplicate entries.
- **🏷️ Language Tabs in Download Modal** — The movie detail page allows switching between languages (Tamil, Malayalam, etc.) to view and download available sizes for each language.
- **🧹 Non-Breaking Whitespace & Title Normalization** — Fixed topic titles containing Unicode non-breaking spaces (`\u00a0`) and non-bracketed year formatting.

---

## 🌟 Features

- **Native C# Architecture** — Built entirely in C# for seamless Jellyfin server integration. No Python or external scripts.
- **Multi-Provider Cloud Orchestration** — Downloads via **Seedr** and **Torbox** APIs.
- **Smart Queue System** — Respects Seedr's 4 GB limit; automatically pools active download sizes and holds pending ones.
- **Zero-Config UI** — Plugin auto-patches `index.html` on startup. Just install and restart.
- **Real-time Progress** — Live download progress and status overlays directly on movie detail pages.
- **Pause / Resume / Stop** — Full download lifecycle control from the movie page.
- **Resilient Metadata** — Saves `.nfo` and locks posters before download to prevent Jellyfin from re-identifying movies when placeholder streams are replaced.
- **History Panel** — View all active, paused, queued, and completed downloads in the plugin settings.

---

## 🛠️ Installation

### Install via Jellyfin Plugin Repository (Recommended)

1. Open your Jellyfin Web UI → **Dashboard** → **Plugins** → **Repositories** tab.
2. Click **+ New Repository** and add:
   - **Name**: `JellyFetch`
   - **URL**: `https://raw.githubusercontent.com/Mohdsuhailpgdi/JellyFetch/main/manifest.json`
3. Click **Save**.
4. Switch to the **Catalog** tab, find **JellyFetch** under General, and click **Install**.
5. **Restart your Jellyfin server.**

That's it. The plugin automatically patches your web interface on first startup. Do a **hard refresh** (`Ctrl + Shift + R`) in your browser to see the Download button.

> **Note for Docker users:** The auto-patcher writes to the Jellyfin web directory inside the container. If your web directory is on a read-only volume mount, the auto-patch will fail silently and you will see a warning in the Jellyfin logs. In that case, see the manual install section below.

---

### Manual Web UI Install (Docker / Read-only web volumes)

If the auto-patch cannot write to the web directory, you can install manually:

```bash
# Download and extract the frontend
curl -L https://github.com/Mohdsuhailpgdi/JellyFetch/releases/download/v1.0.2/dist.tar.gz \
  | sudo tar -xz -C /usr/share/jellyfin/web/

sudo chown -R jellyfin:jellyfin /usr/share/jellyfin/web/
sudo systemctl restart jellyfin
```

Then hard-refresh your browser (`Ctrl + Shift + R`).

---

## ⚙️ Configuration & Usage

After restarting, configure your cloud provider:

1. Go to **Dashboard** → **Plugins** → **JellyFetch** (settings icon).
2. Enter credentials:
   - **Seedr**: Username + Password
   - **Torbox**: API Key
3. Click **Save**.

### How it Works

1. Navigate to any movie in your Jellyfin library.
2. If the movie is a placeholder (`.strm` file / not yet downloaded), you'll see a **Download** button instead of Play.
3. Click **Download**, choose the file size/language variant, and it kicks off.
4. The button transforms into a live progress indicator. You can pause, resume, or stop at any time.
5. When done, the movie page automatically refreshes with full metadata and the Play button appears.

### The History Tab

Inside the Plugin Settings page → **History** tab:
- View all **Active**, **Paused**, and **Queued** downloads.
- Monitor live progress.
- **Pause / Resume / Cancel** any active download.

---

## 💻 Development

```bash
dotnet build -c Release
```

The plugin targets `.NET 8.0`. After building, copy `Jellyfin.Plugin.JellyFetch.dll` into your Jellyfin `/config/plugins/JellyFetch_1.0.2.0/` folder and restart the server.

### Project Structure

```
JellyFetch-Plugin/
├── Plugin.cs                   ← Main plugin entry + web auto-patcher
├── Configuration/              ← PluginConfiguration (Seedr/Torbox credentials)
├── Api/
│   └── DownloadersController.cs ← REST endpoints (/System/Configuration/Downloaders/*)
├── Helpers/
│   ├── JellyfinScraper.cs      ← C# scraper for 1tamilmv and fallbacks
│   └── SeedrHelper.cs          ← Seedr API integration
├── Tasks/
│   └── ScraperScheduledTask.cs ← Background scheduled task
└── Web/
    ├── downloaders.html         ← Plugin settings page
    ├── downloaders.js           ← Plugin settings JS
    └── jellyfetch-inject.js    ← Frontend injection script (auto-deployed to web dir)
```

---

## 🔒 Security Note

This plugin stores your Seedr/Torbox credentials in Jellyfin's plugin configuration (encrypted at rest by Jellyfin's configuration system). Credentials are never logged or transmitted to any third party.
