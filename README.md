<div align="center">
  <img src="icon.jpg" width="150" alt="JellyFetch Plugin Logo">
  
  # JellyFetch — Jellyfin Downloads Plugin
  
  **Automated media scraping, cloud downloading, and library injection for Jellyfin.**
  
  [![Version](https://img.shields.io/badge/version-1.1.0-blue)](https://github.com/Mohdsuhailpgdi/JellyFetch/releases/latest)
  [![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9.x--12.x-orange)](https://jellyfin.org)
  [![Platform](https://img.shields.io/badge/platform-Linux%20%7C%20Docker-lightgrey)](https://github.com/Mohdsuhailpgdi/JellyFetch)
</div>

---

## 🇮🇳 Built for Regional Audiences
This plugin is specifically optimized for Indian media consumers. The default scraping engine is deeply integrated with **1tamilmv**, providing robust, automated tracking and downloading of regional content, with extensive fallback capabilities.

---

## 🌟 Key Features

- **🚀 Native Movie Detail Integration** — Download button seamlessly matches Jellyfin's default flat icon button theme (adjacent to play, favorite, and watched) without layout shifts or color clashes.
- **📊 Real-Time Percentage Progress** — Transforms dynamically into a fixed-width pill (76px) strictly displaying the sync icon and percentage (`🔄 74%`).
- **☁️ Multi-Provider Cloud Orchestration** — Downloads via **Seedr** and **Torbox** APIs with smart quota pooling and automatic queuing.
- **🌐 Multi-Language Movie Merging** — Automatically tracks and merges multi-language releases (Tamil, Malayalam, Telugu, Hindi) under a single movie entry.
- **🛡️ Real Library Deduplication** — Automatically detects movies already present in your Jellyfin library and skips dummy placeholders.
- **⚡ Zero-Config UI Injection** — Plugin auto-patches `index.html` silently on startup. No manual file copying or complicated setup required.
- **🧹 Clean Uninstallation** — Implements Jellyfin's official `OnUninstalling()` lifecycle method to automatically revert `index.html` and remove injected assets when the plugin is removed.
- **🎮 In-Place Control** — Full Pause / Resume / Stop lifecycle management directly from the movie page.
- **🔒 Resilient Metadata** — Automatically preserves `.nfo` files and posters before downloading to prevent re-identification mismatches.
- **📜 Integrated Dashboard & History** — Dedicated configuration panel and real-time history viewer inside the Jellyfin dashboard.

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

The plugin targets `.NET 8.0`. After building, copy `Jellyfin.Plugin.JellyFetch.dll` into your Jellyfin `/config/plugins/JellyFetch_1.1.0.0/` folder and restart the server.

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
