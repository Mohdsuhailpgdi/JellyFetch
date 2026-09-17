# Jellyfin Downloads Plugin

A powerful, native C# Jellyfin plugin that automatically scrapes media sources, orchestrates cloud downloads (Seedr, Torbox), and seamlessly injects them directly into your Jellyfin library.

## Features

* **Native C# Architecture**: Built entirely in C# for seamless integration into the Jellyfin server lifecycle without external dependencies like Python.
* **Multi-Provider Support**: Supports downloading via both **Seedr** and **Torbox** APIs.
* **Smart Queueing System**: Automatically pools the sizes of active downloads and holds pending ones in a queue to prevent exceeding cloud storage limits (e.g., Seedr's 4GB limit).
* **Interactive UI**: Fully integrated into the Jellyfin web interface.
  * Real-time download progress overlays on movie posters.
  * Dedicated History panel in the plugin settings with live active, paused, and queued states.
  * Pause, Resume, and Cancel functionality directly from the UI.
* **Resilient Metadata**: Automatically saves `.nfo` metadata and poster locks before download completion to prevent Jellyfin from incorrectly re-identifying movies when replacing placeholder streams with real video files.
* **Robust Logging & History**: JSON-based history manager prevents SQL lock errors while maintaining a permanent record of all completed, failed, and canceled downloads.
* **Regional Optimization**: Built specifically with Indian users in mind, featuring deep integration and default scrapers optimized for *1tamilmv*, along with extensive fallback capabilities.

## Configuration

To use the plugin, you must configure your cloud provider credentials in the Plugin Settings page inside Jellyfin:

1. **Seedr**: Enter your Seedr Username and Password.
2. **Torbox**: Enter your Torbox API Key.

Once configured, the plugin's background scheduled task will automatically monitor for new media, resolve the magnets, and fetch them directly into your configured Jellyfin download directory.

## Development

The plugin is compiled for `.NET 8.0` to match modern Jellyfin server versions.

```bash
dotnet build
```

After building, copy the `Jellyfin.Plugin.Downloads.dll` into your Jellyfin `/config/plugins/Downloads/` folder and restart the server.
