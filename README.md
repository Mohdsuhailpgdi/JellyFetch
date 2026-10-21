<div align="center">
  <img src="icon.jpg" width="150" alt="JellyFetch Plugin Logo">
  
  # JellyFetch — Jellyfin Downloads Plugin
  
  **Automated media scraping, cloud downloading, and library injection for Jellyfin.**
  
  [![Version](https://img.shields.io/badge/version-1.3.8.7-blue)](https://github.com/Mohdsuhailpgdi/JellyFetch/releases/latest)
  [![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9.x--12.x-orange)](https://jellyfin.org)
  [![Platform](https://img.shields.io/badge/platform-Linux%20%7C%20Docker-lightgrey)](https://github.com/Mohdsuhailpgdi/JellyFetch)
</div>

---

## 🇮🇳 Built for Regional Audiences
This plugin is specifically optimized for Indian media consumers. The default scraping engine is deeply integrated with **1tamilmv**, providing robust, automated tracking and downloading of regional content, with extensive fallback capabilities.

---

## ✨ What's New in v1.3.8.7 — Strict Language Filter Regression Fix & Phase 4 Maintenance
- **Strict Language Filter Enforcement**: Completely removed the subforum trust fallback introduced in `v1.3.8.5` that caused regional dubbed releases (e.g. Kannada, Telugu, Hindi) in Tamil/Malayalam dubbed subforums to bypass strict language rejection and flood the library. TMDB `original_language` is strictly preserved and enforced.
- **Smart Cleanup Original Language Verification**: Smart Cleanup now inspects movie folder names using `DetectLanguageAsync` to detect and delete movies whose original language was unselected, rather than relying solely on magnet audio tags in `downloads.json`.
- **Atomic Database Purge**: "Purge All Scraped Movies" now directly queries `ILibraryManager` and deletes items atomically from Jellyfin's SQLite database, ensuring immediate WebSocket UI updates even when library root folders are empty.
- **FUSE / Cloud History Optimization**: Guarded directory lookups in Activity History to eliminate slow recursive file traversals across Google Drive (`/mnt/gdrive`), preventing API quota exhaustion and UI freezes.
- **Dynamic FFmpeg Resolution & Web Admin Hardening**: Added dynamic FFmpeg fallback to system `PATH` and sanitized all admin table string interpolations with `escapeHtml()`.

<details>
<summary><b>📜 What's New in v1.3.8.6 — Frontend Script Syntax Hotfix</b></summary>

- **Frontend Script Syntax Hotfix**: Resolved a fatal JavaScript `SyntaxError` in `jellyfetch-inject.js` (stray closing braces inside `contextMenuObserver`) that prevented the entire frontend script from executing in `1.3.8.5`, restoring the Download button and Exclude & Remove context menu.
- **Safe State & Route Resolution**: Restored rock-solid hash-based details page detection and safe in-memory `.strm` caching.

</details>

<details>
<summary><b>📜 What's New in v1.3.8.5 — Details Page Teardown, 0ms ActionSheet & Scraper Accuracy</b></summary>

- **Permanent Details Page Teardown**: Fully fixed Exclude & Remove on the Movie Details page. Permanently removes native Play button DOM elements and triggers native `window.history.back()` to prevent play button resurgence and view hang.
- **Instant 0ms ActionSheet Menu Render**: Implemented in-memory `.strm` caching so "Exclude & Remove" renders synchronously (0ms) when opening the 3-dot context menu, completely eliminating rendering delay and pop-in flicker.
- **Year-Aware Metadata Queries & Subforum Fallback**: Added 4-digit release year extraction to TMDB (`&primary_release_year=`) and OMDb (`&y=`) queries, preventing single-word titles (like *Aasha*) from colliding with older classics in unselected languages, plus subforum trust fallback.
- **Chronological Sorting in Recently Added**: Staggered NFO `<dateadded>` timestamps based on topic priority index, ensuring Page 1 releases always sort ahead of older back-catalog items in "Recently Added".

</details>

<details>
<summary><b>📜 What's New in v1.3.8.4 — Exclude & Remove Lifecycle & Multi-Select Fixes</b></summary>

- **Native ActionSheet Close Lifecycle**: Eliminated UI freezes and stuck pages after excluding movies by adhering to Jellyfin Web's natural event bubbling (`chunk 62882` / `dialogHelper`).
- **Multi-Select Batch Exclusion**: Added support for selecting multiple `.strm` movies on library grids and excluding/removing them in parallel with clean selection panel teardown.
- **Details Page Clean Route Navigation**: Eliminated hang states when excluding from a movie details page, smoothly navigating home via `window.Emby.Page.goHome()` and suppressing phantom play buttons.
- **Decoupled .strm Determination**: Ensured Download and Exclude buttons appear reliably on `.strm` media placeholders even if `downloads.json` is missing or unreadable on disk.

</details>

<details>
<summary><b>📜 What's New in v1.3.8.3 — Phase 3 Polish & Stability</b></summary>

- **Atomic Library Removal**: Fixes UI hang and eliminates reappearance of excluded media upon browser refresh by calling `DeleteItem` atomically on the backend.
- **Enhanced Deduplication**: Added TMDB/IMDb ID checks to prevent re-scraping movies already in your library (e.g. *Vishwanath & Sons*).
- **Domain Mirror Continuity**: Integrated `1tamilmv.capital` mirror and dynamic HTTP redirect host tracking to prevent scraper stalls.
- **UTC NFO Timestamps**: Standardized `<dateadded>` to UTC, fixing ordering issues in "Recently Added in Movies".
- **Unified Blocklist Table**: Settings page now displays clean movie titles and TMDB IDs in separate, readable columns.

</details>

---

<details>
<summary><b>📜 What's New in v1.3.3 — Strict Language & Edge Case Fixes</b></summary>

- 🛡️ **TMDB Rate Limiting Resilience** — Scraping a large number of movies previously triggered TMDB "429 Too Many Requests", falling back to OMDB, which occasionally bypassed strict language filters. Added resilient retry logic with delays to guarantee accurate TMDB/OMDB original language verification.
- 🌍 **Unmapped Foreign Language Rejection** — Movies originating in languages completely outside the supported Indian languages (e.g. French, Spanish) are now correctly identified and stringently rejected by the scraper, preventing foreign theater releases from silently passing through as local dubs.
- 🎬 **Cam Print UI Button Fix** — Fixed a bug where movies possessing *only* HDCAM or Pre-DVD prints would cause the "Download" button to disappear from the movie details page, leaving behind a confusing "Play" button. These lower-tier prints are now available for download but automatically sorted to the bottom of the list.

</details>

---

<details>
<summary><b>📜 What's New in v1.3.2 — Phase 2 Metadata & UI Polish</b></summary>

- 🧹 **Sanitize Scheduled Task & UI Fixes** — Integrated "Sanitize Library Metadata" directly into Jellyfin's official background Scheduled Tasks. Fixed UI polling so that progress persists across page reloads and displays as native Jellyfin banners instead of intrusive pop-ups.
- ⚡ **Sanitize Speed Optimization** — Sped up the sanitize task dramatically by using `ffprobe` to check if metadata titles exist first, skipping already-scrubbed files instead of running a redundant `ffmpeg -c copy` on every single media file.
- 🗑️ **File Artifact Cleanup** — Handled and cleaned up `.mkv.tmp.mkv` duplicate extensions caused by in-place metadata edits.
- ☁️ **Prod Migration Exclusions** — Added support for intelligent `rclone` migration exclusion of `.strm` and `.part` dummy files during cloud offloads.

</details>

---

<details>
<summary><b>📜 What's New in v1.2.3 — Task Execution Hardening & Progress Granularity</b></summary>

- 🛡️ **Reflection-Based Task Worker Execution** — Fixed edge cases where triggering Scraper or Cleanup tasks from the plugin dashboard could fail with `InvalidCastException`.
- 🧹 **Fresh Log Panel on Each Run** — Activity logs now auto-clear when a new task starts, preventing stale entries from previous runs from cluttering the live log view.
- ⏳ **Graceful Idle Reset** — After a task completes, the progress indicator holds at `100%` for 10 seconds before quietly resetting to `Idle`, giving users time to read the final status.
- 📊 **Richer Scraper Progress Milestones** — Scraper now reports granular per-stage milestones: domain testing, connection confirmation, front page scan, and per-language subforum scanning with individual progress points.

</details>

<details>
<summary><b>📜 What's New in v1.2.2 — Native Scheduled Tasks & Two-Way Sync</b></summary>

- ⏱️ **Jellyfin Native Scheduled Tasks (`IScheduledTask`)** — Scraper and Library Cleanup are now registered natively into Jellyfin's official TaskManager under the **JellyFetch** category. Run them on automated schedules or trigger them with a single click from Dashboard -> Scheduled Tasks.
- 🔄 **Bidirectional Progress & Log Synchronization** — Starting a task from either Jellyfin's *Scheduled Tasks* menu, the Jellyfin *Active Tasks* dashboard, or the JellyFetch plugin page reflects the exact same real-time progress bars, live console logs, and cancellation status across all interfaces in lockstep.
- 📌 **Smart Tab Memory** — The plugin dashboard remembers your last active tab (`sessionStorage`), preventing jarring jumps back to Settings when navigating.
- 🚀 **Smooth Manual Download Flow** — Initiating a manual magnet download confirms the provider and automatically transitions to the **Activity** tab to watch download progress in real time.

</details>

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
│   ├── ScraperScheduledTask.cs ← Native IScheduledTask: 1TamilMV media scraper
│   └── CleanupScheduledTask.cs ← Native IScheduledTask: Library & .strm cleanup
└── Web/
    ├── downloaders.html        ← Tabbed plugin settings page
    ├── downloaders.js          ← Tabbed settings controller & event handlers
    └── jellyfetch-inject.js    ← Frontend injection script (auto-deployed to web dir)
```

---

## 🔒 Security Note

This plugin stores your Seedr/Torbox credentials in Jellyfin's plugin configuration (encrypted at rest by Jellyfin's configuration system). Credentials are never logged or transmitted to any third party.
