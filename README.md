<div align="center">
  <img src="icon.jpg" width="150" alt="JellyFetch Plugin Logo">
  
  # JellyFetch — Jellyfin Downloads Plugin
  
  **Automated media scraping, cloud downloading, and library injection for Jellyfin.**
  
  [![Version](https://img.shields.io/badge/version-1.0.5.1-blue)](https://github.com/Mohdsuhailpgdi/JellyFetch/releases/latest)
  [![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9.x--12.x-orange)](https://jellyfin.org)
  [![Platform](https://img.shields.io/badge/platform-Linux%20%7C%20Docker-lightgrey)](https://github.com/Mohdsuhailpgdi/JellyFetch)
</div>

---

## 🇮🇳 Built for Regional Audiences
This plugin is specifically optimized for Indian media consumers. The default scraping engine is deeply integrated with **1tamilmv**, providing robust, automated tracking and downloading of regional content, with extensive fallback capabilities.

---

## ✨ What's New in v1.0.5.1

- **🛑 Fixed: Browser Freeze ("Page Unresponsive") on Player Exit** — Eliminated an infinite Promise microtask loop in `syncButton` when exiting the video player, ensuring the player closes instantaneously without freezing the browser or mobile app.
- **🛡️ Clean Uninstallation Lifecycle Hook** — Implemented Jellyfin's official `OnUninstalling()` lifecycle method. When uninstalled from the Jellyfin Dashboard, the plugin automatically removes the `<script>` tag from `index.html` and deletes `jellyfetch-inject.js`, cleanly restoring Jellyfin to stock.
- **⚡ Zero-Recursion Button Sync & Player Lifecycle Guards** — Added `isPlayerActive()` guards to prevent `viewshow`, `viewhide`, and `onPageChange` from injecting buttons while the video player is active.
- **🎨 Native Detail Button Styling** — Movie details Download button matches Jellyfin's default flat icon button theme (identical to checkmark, favorite, and more options buttons).
- **📊 Fixed-Width Percentage Progress Indicator** — Transforms into a sleek fixed-width pill (76px) strictly displaying the sync icon and percentage (`🔄 74%`), eliminating layout jumping.

<details>
<summary><b>📜 Previous Version Highlights (v1.0.3 – v1.0.4)</b></summary>

- **v1.0.4**: Real library deduplication skips dummy `.strm` creation for owned movies. Redesigned Cleanup `.strm` tool with real-time percentage progress and live activity log box.
- **v1.0.3**: Scraper cross-version compatibility for Jellyfin 10.9, 10.10, and 12.1+. Multi-language releases (e.g. Tamil and Malayalam) are cleanly merged under the same title without duplicate entries.
</details>

---

## 🌟 Features

- **Native C# Architecture** — Built entirely in C# for seamless Jellyfin server integration. No Python or external dependencies.
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

> **Note for Docker users:** The auto-patcher writes to the Jellyfin web directory inside the container on startup. If your web directory is mounted read-only, ensure write permissions or mount the web folder as read-write.

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

The plugin targets `.NET 8.0`. After building, copy `Jellyfin.Plugin.JellyFetch.dll` into your Jellyfin `/config/plugins/JellyFetch_1.0.5.1/` folder and restart the server.

### Project Structure

```
JellyFetch-Plugin/
├── Plugin.cs                   ← Main plugin entry, web auto-patcher & OnUninstalling lifecycle hook
├── Configuration/              ← PluginConfiguration (Seedr/Torbox credentials, settings)
├── Api/
│   └── DownloadersController.cs ← REST endpoints (/System/Configuration/Downloaders/*)
├── Helpers/
│   ├── JellyfinScraper.cs      ← C# scraper for 1tamilmv with multi-language merging
│   ├── SeedrHelper.cs          ← Seedr API integration & queue management
│   ├── TorboxHelper.cs         ← Torbox API integration & direct downloading
│   └── DownloadHistoryManager.cs ← JSON history storage & state manager
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
