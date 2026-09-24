<div align="center">
  <img src="icon.jpg" width="150" alt="JellyFetch Plugin Logo">
  
  # JellyFetch — Jellyfin Downloads Plugin
  
  **Automated media scraping, cloud downloading, and library injection for Jellyfin.**
  
  [![Version](https://img.shields.io/badge/version-1.2.2-blue)](https://github.com/Mohdsuhailpgdi/JellyFetch/releases/latest)
  [![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9.x--12.x-orange)](https://jellyfin.org)
  [![Platform](https://img.shields.io/badge/platform-Linux%20%7C%20Docker-lightgrey)](https://github.com/Mohdsuhailpgdi/JellyFetch)
</div>

---

## 🇮🇳 Built for Regional Audiences
This plugin is specifically optimized for Indian media consumers. The default scraping engine is deeply integrated with **1tamilmv**, providing robust, automated tracking and downloading of regional content, with extensive fallback capabilities.

---

## ✨ What's New in v1.2.2 — Native Scheduled Tasks & Dashboard Polish

- ⏱️ **Jellyfin Native Scheduled Tasks (`IScheduledTask`)** — Scraper and Library Cleanup are now registered natively into Jellyfin's official TaskManager under the **JellyFetch** category. Run them on automated schedules or trigger them with a single click.
- 🔄 **Unified Two-Way Progress Synchronization** — Running a task from either Jellyfin's *Scheduled Tasks* menu or the JellyFetch dashboard synchronizes live progress bars, real-time activity logs, and cancellation states seamlessly.
- 📌 **Smart Tab Memory** — The plugin dashboard remembers your last active tab (`sessionStorage`), preventing jarring jumps back to Settings when navigating.
- 🚀 **Smooth Manual Download Flow** — Initiating a manual magnet download confirms the provider and automatically transitions to the **Activity** tab to watch download progress in real time.

---

## ✨ What's New in v1.2.1 (Maintenance Update)

- 📏 **Dynamic File Size Tracking** — Automatically detects and logs exact file sizes across cloud APIs (Seedr/Torbox), HTTP streaming headers (`Content-Length`), and the local filesystem (`FileInfo.Length`).
- 🏷️ **Clean Movie Title Sanitation** — Automatically sanitizes manual download titles by stripping video file extensions (`.mkv`, `.mp4`) and website domain prefixes (`www.1TamilMV.im - `, etc.) both during download and across historical entries.
- 🔍 **Multi-Directory Fallback Resolution** — Retroactively discovers and displays completed file sizes across standard media paths (`/media`, `/media/Downloads`, `/mnt/msp/Movies`).

---

## ✨ What's New in v1.2.0 — Phase 1: Dashboard UI & Manual Downloads

- 📑 **Tabbed Modern Dashboard** — Rebuilt the plugin dashboard into 4 clean, focused tabs (*Providers & Settings*, *Manual Download*, *Scraper & Tasks*, *Activity / History*) with left-aligned inputs and responsive sizing.
- 🧲 **Manual Magnet Downloads** — Directly paste any Magnet URI into the dashboard with your choice of downloader (*Auto-detect*, *Seedr*, or *Torbox*). Automatically redirects to the Activity tab to track download progress.
- 📂 **Standalone Media Fallback** — Manual downloads without a pre-existing `.strm` placeholder cleanly route into the configured Jellyfin Downloads directory without errors.
- 🎨 **Enhanced Button Styling & Micro-Animations** — Download button on movie details now features smooth circular hover plates, scaling animations, and glowing states matching Jellyfin's native buttons (`✓` Watched, `♥` Favorite).
- ⚠️ **Friendly Low-Peer Alert Cards** — Replaced technical jargon ("0 peers") with clear, actionable amber alert cards and glowing amber badges when torrents are inactive or lack seeders.
- 🌐 **Scraper Mirror Auto-Discovery Badge** — Real-time badge shows the latest active domain discovered by the engine, with support for custom backup mirror overrides.
- 🧹 **Robust Language Cleanup Handling** — Unchecking languages now reliably synchronizes to the server before running cleanup, accurately pruning deselected `.strm` files with instant history updates.

<details>
<summary><b>📜 Previous Releases (v1.1.0)</b></summary>

- **v1.1.0 (Initial Official Release)**: Automated 1TamilMV scraping, Seedr/Torbox cloud orchestration, native movie detail injection with fixed-width percentage pills, multi-language title merging, real library deduplication, and zero-config clean uninstallation hooks.
</details>

---

## 🌟 Key Features

- **🚀 Native Movie Detail Integration** — Download button seamlessly matches Jellyfin's default flat icon button theme (adjacent to play, favorite, and watched) without layout shifts or color clashes.
- **📊 Real-Time Percentage Progress** — Transforms dynamically into a fixed-width pill (76px) strictly displaying the sync icon and percentage (`🔄 74%`).
- **🧲 Direct Magnet Ingestion** — Paste custom magnet links straight from the dashboard for on-demand cloud downloading.
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

### The Dashboard Tabs

Inside the Plugin Settings page:
- **Providers & Settings**: Configure cloud accounts (Seedr, Torbox) and storage directories.
- **Manual Download**: Paste magnet links directly and choose your preferred provider.
- **Scraper & Tasks**: Monitor scraper mirrors, configure language filters, and trigger manual scraping or cleanup.
- **Activity**: Monitor live download progress, view historical events, and cancel/resume downloads.

---

## 💻 Development

```bash
dotnet build -c Release
```

The plugin targets `.NET 8.0`. After building, copy `Jellyfin.Plugin.JellyFetch.dll` into your Jellyfin `/config/plugins/JellyFetch_1.2.0.0/` folder and restart the server.

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
    ├── downloaders.html        ← Tabbed plugin settings page
    ├── downloaders.js          ← Tabbed settings controller & event handlers
    └── jellyfetch-inject.js    ← Frontend injection script (auto-deployed to web dir)
```

---

## 🔒 Security Note

This plugin stores your Seedr/Torbox credentials in Jellyfin's plugin configuration (encrypted at rest by Jellyfin's configuration system). Credentials are never logged or transmitted to any third party.
