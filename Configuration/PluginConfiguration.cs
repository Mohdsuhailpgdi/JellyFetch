// JellyFetch Plugin for Jellyfin - Version 1.0.5.1
using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyFetch.Configuration;

/// <summary>
/// The plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Torbox API key.
    /// </summary>
    public string TorboxApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OMDb API key for fallback language checks.
    /// </summary>
    public string OmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Seedr username or API key.
    /// </summary>
    public string SeedrUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Seedr password.
    /// </summary>
    public string SeedrPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether Seedr should be used for downloads <= 4GB.
    /// </summary>
    public bool EnableSeedr { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Torbox should be used for downloads > 4GB.
    /// </summary>
    public bool EnableTorbox { get; set; } = true;

    /// <summary>
    /// Gets or sets the custom downloads directory path.
    /// </summary>
    public string DownloadsDirectory { get; set; } = "/media/Downloads";
}
