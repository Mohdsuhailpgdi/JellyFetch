using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyFetch.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFetch;

/// <summary>
/// The main plugin class. On startup it auto-patches the Jellyfin web directory
/// by writing jellyfetch-inject.js and adding a &lt;script&gt; tag to index.html,
/// so users get the full Download UI without any manual installation steps.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private const string InjectScriptName = "jellyfetch-inject.js";
    private const string ScriptMarker    = "jellyfetch-inject.js"; // used to check if already patched
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger  = loggerFactory.CreateLogger<Plugin>();
        MigrateLegacyData(applicationPaths);
        TryPatchWebDirectory(applicationPaths);
    }

    /// <inheritdoc />
    public override string Name => "JellyFetch";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("A1B2C3D4-E5F6-7890-1234-567890ABCDEF");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "Downloaders",
                EmbeddedResourcePath = GetType().Namespace + ".Web.downloaders.html"
            },
            new PluginPageInfo
            {
                Name = "downloadersjs",
                EmbeddedResourcePath = GetType().Namespace + ".Web.downloaders.js"
            }
        };
    }

    // ─── Web directory auto-patching ────────────────────────────────────────

    /// <summary>
    /// Attempts to locate the Jellyfin web directory, write jellyfetch-inject.js
    /// into it, and patch index.html with a &lt;script&gt; tag — all silently.
    /// Failure is logged as a warning but never crashes the plugin.
    /// </summary>
    private void TryPatchWebDirectory(IApplicationPaths paths)
    {
        try
        {
            var webDir = FindWebDirectory(paths);
            if (webDir is null)
            {
                _logger.LogWarning("[JellyFetch] Could not locate the Jellyfin web directory. " +
                    "Automatic UI injection skipped. See README for manual install.");
                return;
            }

            WriteInjectScript(webDir);
            PatchIndexHtml(webDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JellyFetch] Web directory patching failed (non-fatal).");
        }
    }

    /// <summary>
    /// Returns the Jellyfin web directory, checking several well-known paths.
    /// </summary>
    private string? FindWebDirectory(IApplicationPaths paths)
    {
        // Candidates ordered by likelihood
        var candidates = new List<string>
        {
            // Derive from the server binary location (most reliable)
            Path.Combine(AppContext.BaseDirectory, "jellyfin-web"),
            Path.Combine(AppContext.BaseDirectory, "..", "jellyfin-web"),
            Path.Combine(AppContext.BaseDirectory, "web"),
            // Native Ubuntu/Debian install
            "/usr/share/jellyfin/web",
            // Docker official image
            "/jellyfin/jellyfin-web",
            // Docker linuxserver.io image
            "/app/jellyfin/web",
            "/app/www",
        };

        // Also try sibling "web" next to the data folder
        if (!string.IsNullOrEmpty(paths.DataPath))
        {
            candidates.Insert(0, Path.Combine(Path.GetDirectoryName(paths.DataPath) ?? string.Empty, "web"));
        }

        return candidates
            .Select(p => Path.GetFullPath(p))
            .FirstOrDefault(p => File.Exists(Path.Combine(p, "index.html")));
    }

    /// <summary>
    /// Extracts the embedded jellyfetch-inject.js and writes it to the web directory.
    /// </summary>
    private void WriteInjectScript(string webDir)
    {
        var dest = Path.Combine(webDir, InjectScriptName);
        var resourceName = GetType().Namespace + ".Web." + InjectScriptName.Replace('-', '_').Replace('.', '_');

        // Fallback: try exact resource name
        var asm = Assembly.GetExecutingAssembly();
        var exactName = GetType().Namespace + ".Web.jellyfetch_inject.js";

        // Find the embedded resource by suffix match (handles naming edge-cases)
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("jellyfetch-inject.js", StringComparison.OrdinalIgnoreCase)
                              || n.EndsWith("jellyfetch_inject.js", StringComparison.OrdinalIgnoreCase));

        if (resource is null)
        {
            _logger.LogWarning("[JellyFetch] Embedded resource jellyfetch-inject.js not found in assembly.");
            return;
        }

        using var stream = asm.GetManifestResourceStream(resource)!;
        using var fs     = new FileStream(dest, FileMode.Create, FileAccess.Write);
        stream.CopyTo(fs);

        _logger.LogInformation("[JellyFetch] Wrote {Script} to {Dir}", InjectScriptName, webDir);
    }

    /// <summary>
    /// Patches index.html to include a &lt;script src="jellyfetch-inject.js"&gt; tag.
    /// The patch is idempotent — it will not add the tag a second time.
    /// </summary>
    private void PatchIndexHtml(string webDir)
    {
        var indexPath = Path.Combine(webDir, "index.html");
        var html = File.ReadAllText(indexPath, Encoding.UTF8);

        if (html.Contains(ScriptMarker))
        {
            _logger.LogInformation("[JellyFetch] index.html already contains the inject script — skipping patch.");
            return;
        }

        // Insert just before </body>
        var scriptTag = $"\n    <script src=\"{InjectScriptName}?v={Version}\"></script>";
        var patched   = html.Replace("</body>", scriptTag + "\n</body>");

        if (patched == html)
        {
            // No </body> found — append at end of file as fallback
            patched = html + scriptTag;
        }

        File.WriteAllText(indexPath, patched, Encoding.UTF8);
        _logger.LogInformation("[JellyFetch] Patched index.html in {Dir}", webDir);
    }

    /// <summary>
    /// Migrates credentials, settings, and data files from legacy plugin installations (e.g. Jellyfin.Plugin.Downloads).
    /// </summary>
    private void MigrateLegacyData(IApplicationPaths paths)
    {
        try
        {
            // 1. Migrate credentials & settings from old Jellyfin.Plugin.Downloads.xml if current config is empty
            if (string.IsNullOrWhiteSpace(Configuration.SeedrUsername) && string.IsNullOrWhiteSpace(Configuration.TorboxApiKey))
            {
                var legacyConfigs = new[]
                {
                    Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.Downloads.xml"),
                    Path.Combine(paths.PluginConfigurationsPath, "Downloads.xml")
                };

                foreach (var oldConfigPath in legacyConfigs)
                {
                    if (File.Exists(oldConfigPath))
                    {
                        _logger.LogInformation("[JellyFetch] Found legacy configuration at {Path}. Migrating credentials...", oldConfigPath);
                        try
                        {
                            var doc = XDocument.Load(oldConfigPath);
                            var root = doc.Root;
                            if (root != null)
                            {
                                var seedrUser = root.Element("SeedrUsername")?.Value;
                                var seedrPass = root.Element("SeedrPassword")?.Value;
                                var torboxKey = root.Element("TorboxApiKey")?.Value;
                                var dlDir     = root.Element("DownloadsDirectory")?.Value;
                                var enSeedr   = root.Element("EnableSeedr")?.Value;
                                var enTorbox  = root.Element("EnableTorbox")?.Value;

                                bool updated = false;
                                if (!string.IsNullOrWhiteSpace(seedrUser)) { Configuration.SeedrUsername = seedrUser; updated = true; }
                                if (!string.IsNullOrWhiteSpace(seedrPass)) { Configuration.SeedrPassword = seedrPass; updated = true; }
                                if (!string.IsNullOrWhiteSpace(torboxKey)) { Configuration.TorboxApiKey = torboxKey; updated = true; }
                                if (!string.IsNullOrWhiteSpace(dlDir))     { Configuration.DownloadsDirectory = dlDir; updated = true; }
                                if (bool.TryParse(enSeedr, out var es))   { Configuration.EnableSeedr = es; updated = true; }
                                if (bool.TryParse(enTorbox, out var et))  { Configuration.EnableTorbox = et; updated = true; }

                                if (updated)
                                {
                                    SaveConfiguration();
                                    _logger.LogInformation("[JellyFetch] Legacy configuration successfully migrated and saved.");
                                }
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[JellyFetch] Could not parse legacy config XML from {Path}", oldConfigPath);
                        }
                    }
                }
            }

            // 2. Migrate data folder files (allowed_languages.json, .last_domain, history.json)
            if (!string.IsNullOrEmpty(DataFolderPath))
            {
                Directory.CreateDirectory(DataFolderPath);
                var parentDir = Path.GetDirectoryName(DataFolderPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    var oldDataDirs = new[]
                    {
                        Path.Combine(parentDir, "Jellyfin.Plugin.Downloads"),
                        Path.Combine(parentDir, "Downloads")
                    };

                    var filesToMigrate = new[] { "allowed_languages.json", ".last_domain", "history.json" };

                    foreach (var oldDir in oldDataDirs)
                    {
                        if (Directory.Exists(oldDir))
                        {
                            foreach (var file in filesToMigrate)
                            {
                                var targetFile = Path.Combine(DataFolderPath, file);
                                var sourceFile = Path.Combine(oldDir, file);
                                if (!File.Exists(targetFile) && File.Exists(sourceFile))
                                {
                                    File.Copy(sourceFile, targetFile, overwrite: false);
                                    _logger.LogInformation("[JellyFetch] Migrated {File} from {Source} to {Target}", file, sourceFile, targetFile);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JellyFetch] Legacy data migration encountered an error (non-fatal).");
        }
    }
}
