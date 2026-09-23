using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
    private readonly IApplicationPaths   _applicationPaths;
    private readonly ILogger<Plugin>     _logger;

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
        Instance          = this;
        _applicationPaths = applicationPaths;
        _logger           = loggerFactory.CreateLogger<Plugin>();
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
        var asm  = Assembly.GetExecutingAssembly();

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
            var updated = System.Text.RegularExpressions.Regex.Replace(
                html,
                @"jellyfetch-inject\.js(\?v=[^""]*)?",
                $"{InjectScriptName}?v={Version}");

            if (updated != html)
            {
                File.WriteAllText(indexPath, updated, Encoding.UTF8);
                _logger.LogInformation("[JellyFetch] Updated index.html inject script to version {Version}", Version);
            }
            else
            {
                _logger.LogInformation("[JellyFetch] index.html already contains the inject script v{Version} — skipping patch.", Version);
            }
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

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        try
        {
            var webDir = FindWebDirectory(_applicationPaths);
            if (!string.IsNullOrEmpty(webDir))
            {
                RevertIndexHtml(webDir);
                DeleteInjectScript(webDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JellyFetch] Failed to cleanly revert web files during uninstallation.");
        }

        base.OnUninstalling();
    }

    /// <summary>
    /// Removes the &lt;script src="jellyfetch-inject.js..."&gt; tag from index.html on uninstallation.
    /// </summary>
    private void RevertIndexHtml(string webDir)
    {
        var indexPath = Path.Combine(webDir, "index.html");
        if (!File.Exists(indexPath)) return;

        var html = File.ReadAllText(indexPath, Encoding.UTF8);
        var pattern = @"\s*<script\s+src=""jellyfetch-inject\.js(\?v=[^""]*)?"">\s*</script>";
        var reverted = System.Text.RegularExpressions.Regex.Replace(html, pattern, string.Empty);

        if (reverted != html)
        {
            File.WriteAllText(indexPath, reverted, Encoding.UTF8);
            _logger.LogInformation("[JellyFetch] Cleanly reverted index.html on uninstallation in {Dir}", webDir);
        }
    }

    /// <summary>
    /// Deletes jellyfetch-inject.js from the web directory on uninstallation.
    /// </summary>
    private void DeleteInjectScript(string webDir)
    {
        var dest = Path.Combine(webDir, InjectScriptName);
        if (File.Exists(dest))
        {
            File.Delete(dest);
            _logger.LogInformation("[JellyFetch] Deleted {Script} on uninstallation from {Dir}", InjectScriptName, webDir);
        }
    }
}
