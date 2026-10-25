using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyFetch.Configuration;
using Jellyfin.Plugin.JellyFetch.Helpers;
using Jellyfin.Plugin.JellyFetch.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFetch.Api;

public class DownloadRequest
{
    public string MagnetUri { get; set; } = string.Empty;
    public double SizeGb { get; set; }
    public string ItemPath { get; set; } = string.Empty;
}

public class TestCredentialsRequest
{
    public string? SeedrUsername { get; set; }
    public string? SeedrPassword { get; set; }
    public string? TorboxApiKey { get; set; }
}

public class AddBlocklistRequest
{
    public string Title { get; set; } = string.Empty;
}

public class DownloadProgressInfo
{
    public string ItemId { get; set; } = string.Empty;
    public string MovieName { get; set; } = string.Empty;
    public string Status { get; set; } = "Idle";
    public double Progress { get; set; } = 0;
    public string Error { get; set; } = string.Empty;
    public bool Completed { get; set; } = false;
    public bool IsPaused { get; set; } = false;
    public string? NewItemId { get; set; }
    
    public string Provider { get; set; } = string.Empty;
    public double SizeGb { get; set; } = 0;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>True when the Seedr cloud torrent has had 0 peers for ≥ 3 consecutive poll cycles (~12s).</summary>
    public bool LowPeerWarning { get; set; } = false;

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Threading.CancellationTokenSource? Cts { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Threading.ManualResetEventSlim PauseEvent { get; } = new(true);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? FolderId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? MagnetUri { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? TargetPath { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ItemPath { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Diagnostics.Process? ActiveProcess { get; set; }

    /// <summary>Internal: consecutive zero-peer poll cycles (not serialized to client).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ZeroPeerCycles { get; set; } = 0;
}


[ApiController]
[Authorize]
[Route("System/Configuration/Downloaders")]
[Produces("application/json")]
public class DownloadersController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DownloadersController> _logger;
    private readonly ITaskManager? _taskManager;
    private static System.Threading.CancellationTokenSource? _scrapeCts;
    private static string _scrapeStatus = "Idle";
    private static double _scrapeProgress = 0;
    private static DateTime _scrapeStartTime = DateTime.MinValue;
    private static readonly List<string> _scrapeLogs = new();
    private static readonly object _scrapeLogLock = new();

    private static System.Threading.CancellationTokenSource? _cleanupCts;
    private static string _cleanupStatus = "Idle";
    private static double _cleanupProgress = 0;
    private static DateTime _cleanupStartTime = DateTime.MinValue;
    private static readonly List<string> _cleanupLogs = new();
    private static readonly object _cleanupLogLock = new();

    private static string _metadataStatus = "Idle";
    private static double _metadataProgress = 0;
    private static readonly List<string> _metadataLogs = new();
    private static readonly object _metadataLogLock = new();

    [HttpGet("Inject.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetInjectJs()
    {
        var asm = typeof(Plugin).Assembly;
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("jellyfetch-inject.js", StringComparison.OrdinalIgnoreCase) || 
                                 n.EndsWith("jellyfetch_inject.js", StringComparison.OrdinalIgnoreCase));
        
        if (resource == null) return NotFound();
        var stream = asm.GetManifestResourceStream(resource);
        return File(stream!, "application/javascript");
    }

    public static void ReportScrapeProgress(string msg, double pct)
    {
        if (pct >= 0) _scrapeProgress = Math.Round(pct, 1);
        _scrapeStatus = msg;
        AddScrapeLog(msg);
    }

    public static void ReportCleanupProgress(string msg, double pct)
    {
        if (pct >= 0) _cleanupProgress = Math.Round(pct, 1);
        _cleanupStatus = msg;
        AddCleanupLog(msg);
    }

    public static void ResetScrapeLogs()
    {
        lock (_scrapeLogLock)
        {
            _scrapeLogs.Clear();
        }
    }

    public static void ResetCleanupLogs()
    {
        lock (_cleanupLogLock)
        {
            _cleanupLogs.Clear();
        }
    }

    public static void ReportMetadataProgress(string msg, double pct)
    {
        if (pct >= 0) _metadataProgress = Math.Round(pct, 1);
        _metadataStatus = msg;
        AddMetadataLog(msg);
    }

    public static void ResetMetadataLogs()
    {
        lock (_metadataLogLock)
        {
            _metadataLogs.Clear();
        }
    }

    private static MediaBrowser.Model.Tasks.IScheduledTaskWorker? GetTaskWorker(ITaskManager? taskManager, string taskTypeName, string taskKey)
    {
        if (taskManager == null) return null;
        try
        {
            var prop = taskManager.GetType().GetProperty("ScheduledTasks")
                    ?? typeof(ITaskManager).GetProperty("ScheduledTasks");
            if (prop == null) return null;
            var list = prop.GetValue(taskManager) as System.Collections.IEnumerable;
            if (list == null) return null;
            foreach (var item in list)
            {
                if (item is MediaBrowser.Model.Tasks.IScheduledTaskWorker worker && worker.ScheduledTask != null)
                {
                    var name = worker.ScheduledTask.GetType().Name;
                    var key = worker.ScheduledTask.Key;
                    if (string.Equals(name, taskTypeName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, taskKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return worker;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static bool TryExecuteScheduledTask(ITaskManager? taskManager, MediaBrowser.Model.Tasks.IScheduledTaskWorker worker)
    {
        if (taskManager == null || worker == null) return false;
        try
        {
            taskManager.Execute(worker, new TaskOptions());
            return true;
        }
        catch
        {
            try
            {
                var method = taskManager.GetType().GetMethod("Execute", new[] { typeof(MediaBrowser.Model.Tasks.IScheduledTaskWorker), typeof(TaskOptions) });
                if (method != null)
                {
                    method.Invoke(taskManager, new object[] { worker, new TaskOptions() });
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    private static bool TryCancelScheduledTask(ITaskManager? taskManager, MediaBrowser.Model.Tasks.IScheduledTaskWorker worker)
    {
        if (taskManager == null || worker == null) return false;
        try
        {
            taskManager.Cancel(worker);
            return true;
        }
        catch
        {
            try
            {
                var method = taskManager.GetType().GetMethod("Cancel", new[] { typeof(MediaBrowser.Model.Tasks.IScheduledTaskWorker) });
                if (method != null)
                {
                    method.Invoke(taskManager, new object[] { worker });
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    private static void AddScrapeLog(string msg)
    {
        lock (_scrapeLogLock)
        {
            _scrapeLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (_scrapeLogs.Count > 120) _scrapeLogs.RemoveAt(0);
        }
    }

    private static void AddCleanupLog(string msg)
    {
        lock (_cleanupLogLock)
        {
            _cleanupLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (_cleanupLogs.Count > 120) _cleanupLogs.RemoveAt(0);
        }
    }

    private static void AddMetadataLog(string msg)
    {
        lock (_metadataLogLock)
        {
            _metadataLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (_metadataLogs.Count > 120) _metadataLogs.RemoveAt(0);
        }
    }

    private static readonly ConcurrentDictionary<string, DownloadProgressInfo> _activeDownloads = new(StringComparer.OrdinalIgnoreCase);
    
    private static string NormalizeId(string itemId)
    {
        if (Guid.TryParse(itemId, out Guid g))
        {
            return g.ToString("N");
        }
        return (itemId ?? string.Empty).Trim().ToLowerInvariant();
    }

    public DownloadersController(ILibraryManager libraryManager, ILogger<DownloadersController> logger, ITaskManager? taskManager = null)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _taskManager = taskManager;
    }

    [HttpGet("Options/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [AllowAnonymous]
    public ActionResult GetOptions(string itemId)
    {
        if (!Guid.TryParse(itemId, out Guid id))
        {
            return BadRequest("Invalid ID");
        }
        
        var item = _libraryManager.GetItemById(id);
        if (item == null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound("Media not found.");
        }

        if (!item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound("Media is already downloaded and playable.");
        }

        var filteredOptions = new List<JsonElement>();
        string description = "";
        var config = Plugin.Instance.Configuration;

        var dir = Path.GetDirectoryName(item.Path);
        var jsonPath = !string.IsNullOrEmpty(dir) ? Path.Combine(dir, "downloads.json") : null;
        if (jsonPath != null && System.IO.File.Exists(jsonPath))
        {
            try
            {
                var content = System.IO.File.ReadAllText(jsonPath);
                var options = JsonSerializer.Deserialize<List<JsonElement>>(content) ?? new List<JsonElement>();

                foreach (var opt in options)
                {
                    if (opt.TryGetProperty("dn", out var nameProp) && opt.TryGetProperty("xl_gb", out var sizeProp))
                    {
                        var dn = nameProp.GetString()?.ToLowerInvariant() ?? "";
                        if (sizeProp.TryGetDouble(out var sizeGb))
                        {
                            if (sizeGb <= 4.0 && !config.EnableSeedr && !config.EnableTorbox) continue;
                            if (sizeGb > 4.0 && !config.EnableTorbox) continue;
                            
                            if (dn.Contains("480p") || dn.Contains("360p")) { } // Allow lower resolutions so button appears
                        }
                    }
                    filteredOptions.Add(opt);
                }

                filteredOptions.Sort((a, b) => 
                {
                    double sizeA = a.TryGetProperty("xl_gb", out var sA) ? sA.GetDouble() : 0;
                    double sizeB = b.TryGetProperty("xl_gb", out var sB) ? sB.GetDouble() : 0;
                    string dnA = a.TryGetProperty("dn", out var nA) ? nA.GetString()?.ToLowerInvariant() ?? "" : "";
                    string dnB = b.TryGetProperty("dn", out var nB) ? nB.GetString()?.ToLowerInvariant() ?? "" : "";

                    bool is1080A = dnA.Contains("1080p");
                    bool is1080B = dnB.Contains("1080p");

                    if (is1080A && !is1080B) return -1;
                    if (!is1080A && is1080B) return 1;

                    return sizeA.CompareTo(sizeB);
                });

                if (!config.EnableSeedr && !config.EnableTorbox)
                {
                    description = "All downloaders are currently disabled in JellyFetch settings.";
                }
                else if (filteredOptions.Count == 0 && options.Count > 0)
                {
                    description = "No download options match the current provider filters.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading downloads.json for {ItemId}", itemId);
                description = "Error reading download options from disk.";
            }
        }
        else
        {
            description = "No download options currently found on disk.";
        }

        _logger.LogInformation("GetOptions for {ItemId} returning {Count} options (IsStrm: true)", itemId, filteredOptions.Count);
        return Ok(new { IsStrm = true, Options = filteredOptions, Description = description });
    }
    [HttpPost("Exclude/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Exclude(string itemId)
    {
        if (!Guid.TryParse(itemId, out Guid id)) return BadRequest("Invalid ID");
        var item = _libraryManager.GetItemById(id);
        if (item == null) return NotFound("Media not found.");

        string path = item.Path ?? "";
        string scrapedFileName = "";

        if (!string.IsNullOrEmpty(path))
        {
            // If path points to a file (.strm, .mkv, .mp4, etc.), extract the filename without extension
            string fileWithoutExt = System.IO.Path.GetFileNameWithoutExtension(path);
            string parentDirName = "";
            string? parentDir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parentDir))
            {
                parentDirName = System.IO.Path.GetFileName(parentDir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            }

            // Exclude based strictly on the scraped file/folder name from disk, ignoring any Jellyfin metadata titles
            if (!string.IsNullOrEmpty(fileWithoutExt) &&
                !fileWithoutExt.Equals("movie", StringComparison.OrdinalIgnoreCase) &&
                !fileWithoutExt.Equals("video", StringComparison.OrdinalIgnoreCase) &&
                !fileWithoutExt.Equals("dummy", StringComparison.OrdinalIgnoreCase))
            {
                scrapedFileName = fileWithoutExt;
            }
            else if (!string.IsNullOrEmpty(parentDirName))
            {
                scrapedFileName = parentDirName;
            }
        }

        // Extreme fallback if filesystem path was not present
        if (string.IsNullOrEmpty(scrapedFileName))
        {
            scrapedFileName = item.Name ?? "";
        }

        // Clean the scraped name to get canonical scraped title and release year (e.g. "Biishman (2022)")
        var (cFull, cBase, cYear) = JellyfinScraper.CleanMovieTitle(scrapedFileName);
        string scrapedEntry = !string.IsNullOrEmpty(cFull) ? cFull : scrapedFileName;

        string normTarget = JellyfinScraper.NormalizeKey(scrapedEntry);
        string normBase = !string.IsNullOrEmpty(cBase) ? JellyfinScraper.NormalizeKey(cBase) : normTarget;

        // Check if Jellyfin metadata item name or original title matches the scraped physical file name
        string normItemName = JellyfinScraper.NormalizeKey(item.Name ?? "");
        string normOrigTitle = !string.IsNullOrEmpty(item.OriginalTitle) ? JellyfinScraper.NormalizeKey(item.OriginalTitle) : "";
        bool titlesMatch = (!string.IsNullOrEmpty(normItemName) && (normItemName == normTarget || normItemName == normBase)) ||
                           (!string.IsNullOrEmpty(normOrigTitle) && (normOrigTitle == normTarget || normOrigTitle == normBase));

        string? tmdbId = null;
        if (item.ProviderIds != null && item.ProviderIds.TryGetValue("Tmdb", out var tId))
        {
            tmdbId = tId;
        }

        // If physical filename matches metadata title AND we have a TMDB ID, exclude with DBID
        // If they do NOT match (Jellyfin wrongly auto-identified the movie), exclude with file name only!
        string entry;
        if (titlesMatch && !string.IsNullOrEmpty(tmdbId))
        {
            string displayTitle = !string.IsNullOrEmpty(item.Name) ? item.Name : (!string.IsNullOrEmpty(cFull) ? cFull : scrapedEntry);
            entry = $"{displayTitle} [TMDB: {tmdbId}]";
        }
        else
        {
            entry = scrapedEntry;
        }

        var config = Plugin.Instance.Configuration;
        var blocklist = config.Blocklist?.ToList() ?? new List<string>();

        bool alreadyBlocked = blocklist.Any(b => {
            if (!string.IsNullOrEmpty(tmdbId) && titlesMatch)
            {
                if (b.Equals(tmdbId, StringComparison.OrdinalIgnoreCase) ||
                    b.Contains($"[TMDB: {tmdbId}]", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            string bClean = Regex.Replace(b, @"\s*\[TMDB:\s*\d+\]", "", RegexOptions.IgnoreCase).Trim();
            string normB = JellyfinScraper.NormalizeKey(bClean);
            return normB == normTarget || normB == normBase || b.Equals(entry, StringComparison.OrdinalIgnoreCase);
        });

        if (!alreadyBlocked)
        {
            // If upgrading from plain title to title with TMDB ID
            if (!string.IsNullOrEmpty(tmdbId) && titlesMatch)
            {
                blocklist.RemoveAll(b => {
                    string bClean = Regex.Replace(b, @"\s*\[TMDB:\s*\d+\]", "", RegexOptions.IgnoreCase).Trim();
                    return JellyfinScraper.NormalizeKey(bClean) == normTarget;
                });
            }

            blocklist.Add(entry);
            config.Blocklist = blocklist.ToArray();
            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation("Excluded {ItemId}: Added '{Entry}' to blocklist (titlesMatch: {TitlesMatch}, metadata: '{MetadataTitle}', tmdb: '{TmdbId}').", 
                itemId, entry, titlesMatch, item.Name, tmdbId);
        }

        try
        {
            string configDownDir = Plugin.Instance.Configuration.DownloadsDirectory ?? "/media/Downloads";

            // 1. Tell Jellyfin to remove the item from SQLite DB and filesystem atomically
            try
            {
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
                _logger.LogInformation("Deleted item {ItemId} ({Title}) via LibraryManager.", itemId, entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LibraryManager.DeleteItem threw for {ItemId}, falling back to manual disk cleanup.", itemId);
            }

            // 2. Clean up any leftover empty/dummy directory on disk (e.g. movie.nfo, poster.jpg, downloads.json)
            string? dirPath = !string.IsNullOrEmpty(path) ? System.IO.Path.GetDirectoryName(path) : null;
            if (!string.IsNullOrEmpty(dirPath) && System.IO.Directory.Exists(dirPath))
            {
                if (!dirPath.Equals(configDownDir, StringComparison.OrdinalIgnoreCase) && 
                    !dirPath.Equals(configDownDir.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                    !dirPath.Equals(configDownDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    // Only delete if it contains no real video files (.mkv, .mp4, .avi)
                    if (!System.IO.Directory.EnumerateFiles(dirPath, "*.mkv", System.IO.SearchOption.AllDirectories).Any() &&
                        !System.IO.Directory.EnumerateFiles(dirPath, "*.mp4", System.IO.SearchOption.AllDirectories).Any() &&
                        !System.IO.Directory.EnumerateFiles(dirPath, "*.avi", System.IO.SearchOption.AllDirectories).Any())
                    {
                        try { System.IO.Directory.Delete(dirPath, true); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during Exclude cleanup for item {ItemId}.", itemId);
            return StatusCode(500, "Blocked successfully, but encountered an error during item deletion.");
        }

        return Ok(new { Success = true, Excluded = true, Deleted = true });
    }

    [HttpGet("Status/{itemId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetDownloadStatus(string itemId)
    {
        var normalized = NormalizeId(itemId);
        if (_activeDownloads.TryGetValue(normalized, out var info))
        {
            return Ok(info);
        }
        return Ok(new DownloadProgressInfo { Status = "Idle" });
    }

    [HttpGet("CurrentActive")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetCurrentActiveDownload()
    {
        foreach (var kvp in _activeDownloads)
        {
            var info = kvp.Value;
            if (info != null && !info.Completed && string.IsNullOrEmpty(info.Error) &&
                info.Status != "Idle" && info.Status != "Stopped" && info.Status != "Failed")
            {
                return Ok(new
                {
                    IsDownloading = true,
                    ItemId = info.ItemId,
                    MovieName = info.MovieName,
                    Progress = info.Progress,
                    Status = info.Status,
                    IsPaused = info.IsPaused
                });
            }
        }
        return Ok(new { IsDownloading = false });
    }

    [HttpGet("Languages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetAllowedLanguages()
    {
        var path = System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "allowed_languages.json");
        if (System.IO.File.Exists(path))
        {
            try
            {
                var content = System.IO.File.ReadAllText(path);
                return Content(content, "application/json");
            }
            catch { }
        }
        return Ok(new[] { "Tamil", "Malayalam", "Hindi", "Telugu", "Kannada", "English" });
    }

    [HttpPost("Languages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult SetAllowedLanguages([FromBody] List<string> langs)
    {
        var path = System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "allowed_languages.json");
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(langs));
        return Ok(new { Message = "Allowed languages updated.", Languages = langs });
    }

    [HttpPost("Blocklist")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult AddToBlocklist([FromBody] AddBlocklistRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Title))
        {
            return BadRequest(new { Message = "Title cannot be empty." });
        }

        string rawTitle = request.Title.Trim();
        var config = Plugin.Instance.Configuration;
        var blocklist = config.Blocklist?.ToList() ?? new List<string>();

        if (!blocklist.Any(b => b.Equals(rawTitle, StringComparison.OrdinalIgnoreCase)))
        {
            blocklist.Add(rawTitle);
            config.Blocklist = blocklist.ToArray();
            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation("Manually added '{Title}' to blocklist.", rawTitle);
        }

        return Ok(new { Success = true, Message = $"Added '{rawTitle}' to blocklist." });
    }

    [HttpDelete("Blocklist/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RemoveFromBlocklist(string id)
    {
        var config = Plugin.Instance.Configuration;
        var blocklist = config.Blocklist?.ToList() ?? new List<string>();
        
        string decodedId = System.Net.WebUtility.UrlDecode(id);
        
        if (blocklist.RemoveAll(b => b.Equals(id, StringComparison.OrdinalIgnoreCase) || b.Equals(decodedId, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            config.Blocklist = blocklist.ToArray();
            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation("Removed '{Id}' from blocklist.", decodedId);
        }
        
        return Ok(new { Success = true });
    }

    [HttpDelete("Activity")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ClearActivityHistory()
    {
        await DownloadHistoryManager.ClearHistoryAsync();
        return Ok(new { Message = "History cleared." });
    }

    [HttpGet("History")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> GetDownloadHistory()
    {
        var history = await DownloadHistoryManager.GetHistoryAsync();
        
        var combined = new List<object>();
        
        // Add active downloads
        foreach (var kvp in _activeDownloads)
        {
            var active = kvp.Value;
            if (active != null && !active.Completed && string.IsNullOrEmpty(active.Error) && active.Status != "Idle" && active.Status != "Stopped" && active.Status != "Failed")
            {
                combined.Add(new {
                    Timestamp = active.Timestamp,
                    MovieName = active.MovieName,
                    SizeGb = active.SizeGb,
                    Provider = active.Provider,
                    Status = active.IsPaused ? "Paused" : active.Status,
                    ItemId = active.ItemId,
                    IsActive = true
                });
            }
        }
        
        var config = Plugin.Instance.Configuration;
        bool needsSave = false;

        // Gather local candidate dirs (strictly local buffer paths; NEVER remote cloud FUSE mounts like /mnt/gdrive)
        var candidateDirs = new List<string>();
        if (!string.IsNullOrEmpty(config.DownloadsDirectory) && Directory.Exists(config.DownloadsDirectory) && !config.DownloadsDirectory.Contains("gdrive", StringComparison.OrdinalIgnoreCase))
        {
            candidateDirs.Add(config.DownloadsDirectory);
        }
        string[] fallbackDirs = { "/media", "/media/Downloads", "/mnt/msp/Movies" };
        foreach (var fb in fallbackDirs)
        {
            if (Directory.Exists(fb) && !candidateDirs.Contains(fb, StringComparer.OrdinalIgnoreCase)) candidateDirs.Add(fb);
        }

        // Pre-gather local files ONCE only if any history entry needs name/size resolution
        List<FileInfo>? localFiles = null;
        bool anyEntryNeedsResolution = history.Any(h => h.Status == "Completed" && (h.SizeGb <= 0 || h.MovieName == "Manual Download" || h.MovieName.Contains("1TamilMV", StringComparison.OrdinalIgnoreCase)));
        if (anyEntryNeedsResolution && candidateDirs.Count > 0)
        {
            localFiles = new List<FileInfo>();
            foreach (var dir in candidateDirs)
            {
                try
                {
                    localFiles.AddRange(new DirectoryInfo(dir).GetFiles("*.*", SearchOption.AllDirectories));
                }
                catch { }
            }
        }

        // Add past history
        foreach (var h in history)
        {
            double entrySize = h.SizeGb;
            string movieName = CleanMediaTitle(h.MovieName);

            if (h.Status == "Completed" && localFiles != null && (entrySize <= 0 || h.MovieName == "Manual Download" || h.MovieName.Contains("1TamilMV", StringComparison.OrdinalIgnoreCase)))
            {
                FileInfo? bestMatch = null;
                if (!string.IsNullOrEmpty(h.MovieName) && h.MovieName != "Manual Download")
                {
                    var cleanHName = CleanMediaTitle(h.MovieName);
                    bestMatch = localFiles.FirstOrDefault(f => CleanMediaTitle(f.Name).Equals(cleanHName, StringComparison.OrdinalIgnoreCase) ||
                                                              f.Name.Contains(cleanHName, StringComparison.OrdinalIgnoreCase));
                }
                if (bestMatch == null)
                {
                    bestMatch = localFiles
                        .Where(f => Math.Abs((f.CreationTimeUtc - h.Timestamp).TotalMinutes) < 15 || Math.Abs((f.LastWriteTimeUtc - h.Timestamp).TotalMinutes) < 15)
                        .OrderBy(f => Math.Abs((f.LastWriteTimeUtc - h.Timestamp).TotalMinutes))
                        .FirstOrDefault();
                }
                if (bestMatch != null && bestMatch.Length > 0)
                {
                    if (entrySize <= 0)
                    {
                        entrySize = Math.Round((double)bestMatch.Length / (1024.0 * 1024.0 * 1024.0), 2);
                        h.SizeGb = entrySize;
                        needsSave = true;
                    }
                    if (h.MovieName == "Manual Download" || h.MovieName.Contains("1TamilMV", StringComparison.OrdinalIgnoreCase) || h.MovieName.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                    {
                        movieName = CleanMediaTitle(bestMatch.Name);
                        h.MovieName = movieName;
                        needsSave = true;
                    }
                }
            }

            combined.Add(new {
                Timestamp = h.Timestamp,
                MovieName = movieName,
                SizeGb = entrySize,
                Provider = h.Provider,
                Status = h.Status,
                ErrorMessage = h.ErrorMessage,
                IsActive = false
            });
        }

        if (needsSave)
        {
            _ = Task.Run(async () => {
                try { await DownloadHistoryManager.SaveAllEntriesAsync(history); } catch { }
            });
        }
        
        // Sort descending by timestamp
        var sorted = combined.OrderByDescending(x => (DateTime)x.GetType().GetProperty("Timestamp").GetValue(x, null)).ToList();
        
        return Ok(sorted);
    }

    [HttpPost("Pause/{itemId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult PauseDownload(string itemId)
    {
        var normalized = NormalizeId(itemId);
        if (_activeDownloads.TryGetValue(normalized, out var info) && !info.Completed && string.IsNullOrEmpty(info.Error) && info.Status != "Stopped" && info.Status != "Failed")
        {
            info.IsPaused = true;
            info.PauseEvent.Reset();
            info.Status = "Download Paused";
            return Ok(info);
        }
        return NotFound("No active download found to pause.");
    }

    [HttpPost("Resume/{itemId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ResumeDownload(string itemId)
    {
        var normalized = NormalizeId(itemId);
        if (_activeDownloads.TryGetValue(normalized, out var info) && !info.Completed && string.IsNullOrEmpty(info.Error) && info.Status != "Stopped" && info.Status != "Failed")
        {
            info.IsPaused = false;
            info.PauseEvent.Set();
            info.Status = "Downloading to Jellyfin library...";
            return Ok(info);
        }
        return NotFound("No active download found to resume.");
    }

    [HttpGet("Domain")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetDomain()
    {
        string lastDomain = "1tamilmv.meme";
        var path = System.IO.Path.Combine(Plugin.Instance.DataFolderPath, ".last_domain");
        if (System.IO.File.Exists(path))
        {
            try
            {
                var content = System.IO.File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(content)) lastDomain = content;
            }
            catch { }
        }

        string customOverride = "";
        var overridePath = System.IO.Path.Combine(Plugin.Instance.DataFolderPath, ".custom_domain");
        if (System.IO.File.Exists(overridePath))
        {
            try
            {
                var content = System.IO.File.ReadAllText(overridePath).Trim();
                if (!string.IsNullOrEmpty(content)) customOverride = content;
            }
            catch { }
        }

        return Ok(new { Domain = lastDomain, Override = customOverride });
    }

    [HttpPost("Domain")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult SetDomain([FromBody] JsonElement body)
    {
        string? domain = null;
        if (body.TryGetProperty("domain", out var d) || body.TryGetProperty("Domain", out d))
        {
            domain = d.GetString();
        }

        var overridePath = System.IO.Path.Combine(Plugin.Instance.DataFolderPath, ".custom_domain");
        var dir = Path.GetDirectoryName(overridePath);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        if (string.IsNullOrWhiteSpace(domain))
        {
            if (System.IO.File.Exists(overridePath)) System.IO.File.Delete(overridePath);
            _logger.LogInformation("Cleared 1TamilMV custom domain override. Auto-discovery will be used.");

            string activeDomain = "1tamilmv.meme";
            var lastPath = System.IO.Path.Combine(Plugin.Instance.DataFolderPath, ".last_domain");
            if (System.IO.File.Exists(lastPath))
            {
                try { activeDomain = System.IO.File.ReadAllText(lastPath).Trim(); } catch { }
            }
            return Ok(new { Message = "Custom override cleared. Auto-detection active.", Domain = activeDomain, Override = "" });
        }

        domain = domain.Trim().ToLowerInvariant().Replace("https://", "").Replace("http://", "").TrimEnd('/');
        System.IO.File.WriteAllText(overridePath, domain);

        var lastDomainPath = System.IO.Path.Combine(Plugin.Instance.DataFolderPath, ".last_domain");
        System.IO.File.WriteAllText(lastDomainPath, domain);

        _logger.LogInformation("Updated 1TamilMV domain override to {Domain}", domain);
        return Ok(new { Message = "Domain override saved.", Domain = domain, Override = domain });
    }

    [HttpPost("Stop/{itemId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult StopDownload(string itemId)
    {
        var normalized = NormalizeId(itemId);
        DownloadProgressInfo? info = null;
        string matchedKey = normalized;

        if (!_activeDownloads.TryGetValue(normalized, out info))
        {
            foreach (var kvp in _activeDownloads)
            {
                if (kvp.Value != null && (
                    kvp.Key.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Value.ItemId.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                    (!kvp.Value.Completed && kvp.Value.Status != "Stopped" && kvp.Value.Status != "Failed")))
                {
                    info = kvp.Value;
                    matchedKey = kvp.Key;
                    break;
                }
            }
        }

        if (info != null)
        {
            _logger.LogInformation("Stopping download for itemId {ItemId} ({MovieName})", matchedKey, info.MovieName);

            try
            {
                if (info.ActiveProcess != null && !info.ActiveProcess.HasExited)
                {
                    info.ActiveProcess.Kill(true);
                    _logger.LogInformation("Killed active helper process for {MovieName}", info.MovieName);
                }
            }
            catch (Exception procEx)
            {
                _logger.LogWarning(procEx, "Could not kill active helper process");
            }

            try { info.Cts?.Cancel(); } catch { }
            try { info.PauseEvent.Set(); } catch { }

            info.Status = "Stopped";
            info.Error = "Download stopped by user.";
            info.Progress = 0;

            _activeDownloads.TryRemove(matchedKey, out _);
            if (!string.IsNullOrEmpty(info.ItemId))
            {
                _activeDownloads.TryRemove(info.ItemId, out _);
                _activeDownloads.TryRemove(NormalizeId(info.ItemId), out _);
            }

            var targetPath = info.TargetPath;
            var itemPath = info.ItemPath;
            var folderId = info.FolderId;
            var magUri = info.MagnetUri;

            _ = Task.Run(async () =>
            {
                if (!string.IsNullOrEmpty(targetPath))
                {
                    var partFile = targetPath + ".part";
                    if (System.IO.File.Exists(partFile))
                    {
                        try { System.IO.File.Delete(partFile); } catch { }
                    }
                    if (System.IO.File.Exists(targetPath))
                    {
                        try { System.IO.File.Delete(targetPath); } catch { }
                    }
                }

                if (!string.IsNullOrEmpty(itemPath))
                {
                    try
                    {
                        if (!System.IO.File.Exists(itemPath))
                        {
                            var dir = Path.GetDirectoryName(itemPath);
                            if (dir != null && Directory.Exists(dir))
                            {
                                await System.IO.File.WriteAllTextAsync(itemPath, "http://localhost:8096\n");
                                _logger.LogInformation("Re-created dummy .strm file at {ItemPath}", itemPath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to check/recreate strm file at {ItemPath}", itemPath);
                    }
                }

                var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
                if (!string.IsNullOrEmpty(config.SeedrUsername) && !string.IsNullOrEmpty(config.SeedrPassword))
                {
                    try
                    {
                        var fid = string.IsNullOrEmpty(folderId) ? "null" : folderId;
                        var mag = string.IsNullOrEmpty(magUri) ? "null" : magUri;
                        _logger.LogInformation("Cleaning up Seedr storage and active torrents for stopped download");
                        using var client = new HttpClient();
                        var helper = new SeedrHelper(client);
                        await helper.CleanupAsync(config.SeedrUsername, config.SeedrPassword, fid, mag, System.Threading.CancellationToken.None);
                    }
                    catch (Exception seedrEx)
                    {
                        _logger.LogWarning(seedrEx, "Seedr cleanup on stop encountered an issue");
                    }
                }
            });

            return Ok(new { Message = "Download stopped and cleaned up successfully." });
        }

        return Ok(new { Message = "No active download to stop." });
    }

    [HttpPost("Download/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult DownloadMedia(string itemId, [FromBody] DownloadRequest req)
    {
        if (!Guid.TryParse(itemId, out Guid id))
        {
            return BadRequest("Invalid ID");
        }
        
        var item = _libraryManager.GetItemById(id);
        if (item == null || string.IsNullOrEmpty(item.Path))
        {
            return NotFound("Media not found.");
        }

        string normalizedId = NormalizeId(itemId);

        if (_activeDownloads.TryGetValue(normalizedId, out var active) &&
            active != null && !active.Completed && string.IsNullOrEmpty(active.Error) &&
            active.Status != "Idle" && active.Status != "Stopped" && active.Status != "Failed")
        {
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                Message = $"A download is already in progress for '{active.MovieName}'.",
                ActiveItemId = active.ItemId,
                ActiveMovieName = active.MovieName,
                ActiveProgress = active.Progress,
                ActiveStatus = active.Status
            });
        }
        string itemPath = item.Path;
        string magnetUri = req.MagnetUri;
        double magnetSize = req.SizeGb;

        string tmdbId = "";
        string imdbId = "";
        if (item.ProviderIds != null)
        {
            item.ProviderIds.TryGetValue("Tmdb", out tmdbId);
            item.ProviderIds.TryGetValue("Imdb", out imdbId);
        }
        
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var cts = new System.Threading.CancellationTokenSource();
        
        if (magnetSize <= 4.0 && config.EnableSeedr)
        {
            if (string.IsNullOrEmpty(config.SeedrUsername) || string.IsNullOrEmpty(config.SeedrPassword))
            {
                return BadRequest(new { Message = "File is <= 4GB and Seedr is enabled, but Seedr account is not configured." });
            }

            var progress = new DownloadProgressInfo
            {
                ItemId = normalizedId,
                MovieName = item.Name ?? "Movie",
                Status = "Connecting to Seedr...",
                Progress = 10,
                Provider = "Seedr",
                SizeGb = magnetSize,
                Timestamp = DateTime.UtcNow,
                Cts = cts,
                ItemPath = itemPath,
                MagnetUri = magnetUri
            };
            _activeDownloads[normalizedId] = progress;

            _ = ProcessSeedrDownload(progress, config, itemPath, magnetUri, magnetSize, tmdbId, imdbId);
        }
        else if (config.EnableTorbox)
        {
            if (string.IsNullOrEmpty(config.TorboxApiKey))
            {
                return BadRequest(new { Message = "Torbox API key is not configured." });
            }

            var progress = new DownloadProgressInfo
            {
                ItemId = normalizedId,
                MovieName = item.Name ?? "Movie",
                Status = "Connecting to Torbox...",
                Progress = 10,
                Provider = "Torbox",
                SizeGb = magnetSize,
                Timestamp = DateTime.UtcNow,
                Cts = cts,
                ItemPath = itemPath
            };
            _activeDownloads[normalizedId] = progress;

            _ = ProcessTorboxDownload(progress, config, itemPath, magnetUri, magnetSize, tmdbId, imdbId);
        }
        else
        {
            return BadRequest(new { Message = $"File size {magnetSize}GB exceeds maximum allowed size (10GB)." });
        }

        return Ok(new { Message = "Download requested successfully!" });
    }
    
    private async Task ProcessSeedrDownload(DownloadProgressInfo progress, PluginConfiguration config, string itemPath, string magnetUri, double magnetSize, string? tmdbId, string? imdbId)
    {
        string normalizedId = progress.ItemId;
        await Task.Yield();
        var activeFolderIds = _activeDownloads.Values
                        .Where(d => d.FolderId != null && !d.Completed && string.IsNullOrEmpty(d.Error) && d.Status != "Stopped" && d.Status != "Failed")
                        .Select(d => d.FolderId!)
                        .ToList();
        try
                {
                    _logger.LogInformation("Starting Seedr download for {ItemPath} with magnet {Magnet}", itemPath, magnetUri);
                    progress.Status = "Caching in Seedr cloud...";
                    progress.Progress = 20;

                    using var httpClient = new HttpClient();
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36");
                    var seedrHelper = new SeedrHelper(httpClient);
                    
                    

                    var res = await seedrHelper.GetDownloadUrlAsync(
                        config.SeedrUsername, 
                        config.SeedrPassword, 
                        magnetUri, 
                        magnetSize, 
                        (pct, msg) => { 
                            var newProgress = Math.Round(pct * 0.3, 1);
                            if (newProgress > progress.Progress) progress.Progress = newProgress;
                            if (!progress.IsPaused) progress.Status = msg;
                        }, 
                        activeFolderIds,
                        progress.MovieName,
                        progress.Cts.Token,
                        (isLowPeer) => { progress.LowPeerWarning = isLowPeer; }); // Item #7 -- Low-Peer Warning

                    if (res.Success)
                    {
                        var url = res.DownloadUrl;
                        var vname = res.VideoName;
                        var fId = res.FolderId;
                        progress.FolderId = fId;

                        if (res.SizeBytes > 0 && progress.SizeGb <= 0)
                        {
                            progress.SizeGb = Math.Round((double)res.SizeBytes / (1024.0 * 1024.0 * 1024.0), 2);
                        }
                        if (!string.IsNullOrEmpty(vname))
                        {
                            progress.MovieName = CleanMediaTitle(vname);
                        }
                        
                        string? targetDir = null;
                        if (string.IsNullOrEmpty(itemPath))
                        {
                            targetDir = config.DownloadsDirectory;
                            if (string.IsNullOrEmpty(targetDir)) targetDir = "/media/Downloads";
                            if (!Directory.Exists(targetDir)) {
                                try { Directory.CreateDirectory(targetDir); } catch { }
                            }
                        }
                        else
                        {
                            targetDir = Path.GetDirectoryName(itemPath);
                        }

                        if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
                        {
                            var fileName = CleanMediaFileName(vname);
                            var targetPath = Path.Combine(targetDir, fileName);
                            progress.TargetPath = targetPath;
                            progress.Status = "Downloading to Jellyfin library...";
                            
                            await StreamWithProgressAsync(url, targetPath, progress, progress.Cts.Token);
                            
                            progress.Status = "Sanitizing metadata...";
                            await SanitizeVideoMetadataAsync(targetPath, _logger);
                            progress.Progress = 90;
                            
                            var nfoPath = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(fileName) + ".nfo");
                            bool hasIds = !string.IsNullOrEmpty(tmdbId) || !string.IsNullOrEmpty(imdbId);
                            if (hasIds && !System.IO.File.Exists(nfoPath))
                            {
                                var dateAddedStr = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                                var nfoContent = "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<movie>\n";
                                if (!string.IsNullOrEmpty(tmdbId)) nfoContent += $"  <tmdbid>{tmdbId}</tmdbid>\n";
                                if (!string.IsNullOrEmpty(imdbId)) nfoContent += $"  <imdbid>{imdbId}</imdbid>\n";
                                nfoContent += $"  <dateadded>{dateAddedStr}</dateadded>\n";
                                nfoContent += "</movie>";
                                try { System.IO.File.WriteAllText(nfoPath, nfoContent); } catch { }
                            }
                            
                            if (System.IO.File.Exists(itemPath) && !itemPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                            {
                                try { System.IO.File.Delete(itemPath); } catch { }
                            }

                            try
                            {
                                await seedrHelper.CleanupAsync(config.SeedrUsername, config.SeedrPassword, fId, null, System.Threading.CancellationToken.None);
                            } catch { }
                        }

                        progress.Status = "Scanning library...";
                        progress.Progress = 95;
                        await _libraryManager.ValidateMediaLibrary(new Progress<double>(), System.Threading.CancellationToken.None);
                        
                        try
                        {
                            var finalTarget = progress.TargetPath ?? "";
                            BaseItem? newItem = null;
                            for (int retry = 0; retry < 5; retry++)
                            {
                                newItem = _libraryManager.FindByPath(finalTarget, false);
                                if (newItem != null) break;
                                await Task.Delay(2000);
                            }

                            if (newItem != null)
                            {
                                progress.NewItemId = newItem.Id.ToString("N");
                                _logger.LogInformation("Resolved newly scanned item ID {NewItemId} for {Target}", progress.NewItemId, finalTarget);
                            }
                            else
                            {
                                _logger.LogWarning("Could not find new item ID after scan for {Target}", finalTarget);
                            }
                        }
                        catch (Exception findEx)
                        {
                            _logger.LogWarning(findEx, "Could not find new item ID after scan");
                        }

                        progress.Status = "Completed";
                        progress.Progress = 100;
                        progress.Completed = true;
                        double finalSize = progress.SizeGb > 0 ? progress.SizeGb : magnetSize;
                        if (finalSize <= 0 && !string.IsNullOrEmpty(progress.TargetPath) && System.IO.File.Exists(progress.TargetPath))
                        {
                            try { finalSize = Math.Round((double)new FileInfo(progress.TargetPath).Length / (1024.0 * 1024.0 * 1024.0), 2); progress.SizeGb = finalSize; } catch { }
                        }
                        _ = DownloadHistoryManager.AddEntryAsync(new DownloadHistoryEntry { MovieName = progress.MovieName, SizeGb = finalSize, Provider = "Seedr", Status = "Completed" });
                        _ = Task.Delay(15000).ContinueWith(t => _activeDownloads.TryRemove(normalizedId, out var ignored));
                    }
                    else
                    {
                        _logger.LogError("Seedr download failed for {ItemPath}: {Error}", itemPath, res.Error);
                        progress.Status = "Failed";
                        progress.Error = res.Error ?? "Unknown Seedr error.";
                        double finalSize = progress.SizeGb > 0 ? progress.SizeGb : magnetSize;
                        _ = DownloadHistoryManager.AddEntryAsync(new DownloadHistoryEntry { MovieName = progress.MovieName, SizeGb = finalSize, Provider = "Seedr", Status = "Failed", ErrorMessage = progress.Error });
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Seedr download cancelled for {ItemPath}", itemPath);
                    progress.Status = "Stopped";
                    double finalSize = progress.SizeGb > 0 ? progress.SizeGb : magnetSize;
                    _ = DownloadHistoryManager.AddEntryAsync(new DownloadHistoryEntry { MovieName = progress.MovieName, SizeGb = finalSize, Provider = "Seedr", Status = "Stopped" });
                    var partFile = (progress.TargetPath ?? "") + ".part";
                    if (System.IO.File.Exists(partFile))
                    {
                        try { System.IO.File.Delete(partFile); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing Seedr download for {ItemPath}", itemPath);
                    progress.Status = "Failed";
                    progress.Error = ex.Message;
                    double finalSize = progress.SizeGb > 0 ? progress.SizeGb : magnetSize;
                    _ = DownloadHistoryManager.AddEntryAsync(new DownloadHistoryEntry { MovieName = progress.MovieName, SizeGb = finalSize, Provider = "Seedr", Status = "Failed", ErrorMessage = ex.Message });
                }
    }

    private async Task ProcessTorboxDownload(DownloadProgressInfo progress, PluginConfiguration config, string itemPath, string magnetUri, double magnetSize, string? tmdbId, string? imdbId)
    {
        string normalizedId = progress.ItemId;
        await Task.Yield();
        try
                {
                    _logger.LogInformation("Starting Torbox download for {ItemPath} with magnet {Magnet}", itemPath, magnetUri);
                    progress.Status = "Caching in Torbox cloud...";
                    progress.Progress = 20;

                    using var httpClient = new HttpClient();
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36");
                    var torboxHelper = new TorboxHelper(httpClient);
                    
                    var res = await torboxHelper.GetDownloadUrlAsync(
                        config.TorboxApiKey, 
                        magnetUri, 
                        progress.Cts.Token);

                    if (res.Success)
                    {
                        var finalUrl = res.DownloadUrl;
                        var fileName = CleanMediaFileName(res.VideoName);

                        if (res.SizeBytes > 0 && progress.SizeGb <= 0)
                        {
                            progress.SizeGb = Math.Round((double)res.SizeBytes / (1024.0 * 1024.0 * 1024.0), 2);
                        }
                        if (!string.IsNullOrEmpty(res.VideoName))
                        {
                            progress.MovieName = CleanMediaTitle(res.VideoName);
                        }
                        
                        string? targetDir = null;
                        if (string.IsNullOrEmpty(itemPath))
                        {
                            targetDir = config.DownloadsDirectory;
                            if (string.IsNullOrEmpty(targetDir)) targetDir = "/media/Downloads";
                            if (!Directory.Exists(targetDir)) {
                                try { Directory.CreateDirectory(targetDir); } catch { }
                            }
                        }
                        else
                        {
                            targetDir = Path.GetDirectoryName(itemPath);
                        }

                        if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
                        {
                            var targetPath = Path.Combine(targetDir, fileName);
                            progress.TargetPath = targetPath;
                            progress.Status = "Downloading to Jellyfin library...";
                            progress.Progress = 30;

                            await StreamWithProgressAsync(finalUrl, targetPath, progress, progress.Cts.Token);
                            
                            _logger.LogInformation("Successfully saved media file: {Target}", targetPath);
                            progress.Status = "Sanitizing metadata...";
                            await SanitizeVideoMetadataAsync(targetPath, _logger);
                            progress.Progress = 90;
                            
                            var nfoPath = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(fileName) + ".nfo");
                            bool hasIds = !string.IsNullOrEmpty(tmdbId) || !string.IsNullOrEmpty(imdbId);
                            if (hasIds && !System.IO.File.Exists(nfoPath))
                            {
                                var dateAddedStr = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                                var nfoContent = "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<movie>\n";
                                if (!string.IsNullOrEmpty(tmdbId)) nfoContent += $"  <tmdbid>{tmdbId}</tmdbid>\n";
                                if (!string.IsNullOrEmpty(imdbId)) nfoContent += $"  <imdbid>{imdbId}</imdbid>\n";
                                nfoContent += $"  <dateadded>{dateAddedStr}</dateadded>\n";
                                nfoContent += "</movie>";
                                try { System.IO.File.WriteAllText(nfoPath, nfoContent); } catch { }
                            }
                            
                            if (System.IO.File.Exists(itemPath) && !itemPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                            {
                                try { System.IO.File.Delete(itemPath); } catch { }
                            }
                            
                            progress.Status = "Scanning library...";
                            progress.Progress = 95;
                            await _libraryManager.ValidateMediaLibrary(new Progress<double>(), System.Threading.CancellationToken.None);

                            try
                            {
                                var finalTarget = targetPath ?? "";
                                BaseItem? newItem = null;
                                for (int retry = 0; retry < 5; retry++)
                                {
                                    newItem = _libraryManager.FindByPath(finalTarget, false);
                                    if (newItem != null) break;
                                    await Task.Delay(2000);
                                }

                                if (newItem != null)
                                {
                                    progress.NewItemId = newItem.Id.ToString("N");
                                }
                            }
                            catch (Exception findEx)
                            {
                                _logger.LogWarning(findEx, "Could not find new item ID after scan");
                            }

                            progress.Status = "Completed";
                            progress.Progress = 100;
                            progress.Completed = true;
                            double finalSize = progress.SizeGb > 0 ? progress.SizeGb : magnetSize;
                            if (finalSize <= 0 && !string.IsNullOrEmpty(progress.TargetPath) && System.IO.File.Exists(progress.TargetPath))
                            {
                                try { finalSize = Math.Round((double)new FileInfo(progress.TargetPath).Length / (1024.0 * 1024.0 * 1024.0), 2); progress.SizeGb = finalSize; } catch { }
                            }
                            _ = DownloadHistoryManager.AddEntryAsync(new DownloadHistoryEntry { MovieName = progress.MovieName, SizeGb = finalSize, Provider = "Torbox", Status = "Completed" });
                            _ = Task.Delay(15000).ContinueWith(t => _activeDownloads.TryRemove(normalizedId, out var ignored));
                        }
                    }
                    else
                    {
                        _logger.LogError("Torbox download failed for {ItemPath}: {Error}", itemPath, res.Error);
                        progress.Status = "Failed";
                        progress.Error = res.Error ?? "Torbox download failed.";
                        double finalSize = progress.SizeGb > 0 ? progress.SizeGb : magnetSize;
                        _ = DownloadHistoryManager.AddEntryAsync(new DownloadHistoryEntry { MovieName = progress.MovieName, SizeGb = finalSize, Provider = "Torbox", Status = "Failed", ErrorMessage = progress.Error });
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Torbox download cancelled for {ItemPath}", itemPath);
                    progress.Status = "Stopped";
                    double finalSize = progress.SizeGb > 0 ? progress.SizeGb : magnetSize;
                    _ = DownloadHistoryManager.AddEntryAsync(new DownloadHistoryEntry { MovieName = progress.MovieName, SizeGb = finalSize, Provider = "Torbox", Status = "Stopped" });
                    var partFile = (progress.TargetPath ?? "") + ".part";
                    if (System.IO.File.Exists(partFile))
                    {
                        try { System.IO.File.Delete(partFile); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing Torbox download for {ItemPath}", itemPath);
                    progress.Status = "Failed";
                    progress.Error = ex.Message;
                    double finalSize = progress.SizeGb > 0 ? progress.SizeGb : magnetSize;
                    _ = DownloadHistoryManager.AddEntryAsync(new DownloadHistoryEntry { MovieName = progress.MovieName, SizeGb = finalSize, Provider = "Torbox", Status = "Failed", ErrorMessage = ex.Message });
                }
    }


    private static string GetFfprobePath()
    {
        if (System.IO.File.Exists("/usr/lib/jellyfin-ffmpeg/ffprobe")) return "/usr/lib/jellyfin-ffmpeg/ffprobe";
        return "ffprobe";
    }

    private static string GetFfmpegPath()
    {
        if (System.IO.File.Exists("/usr/lib/jellyfin-ffmpeg/ffmpeg")) return "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        return "ffmpeg";
    }

    public static async Task SanitizeVideoMetadataAsync(string filePath, ILogger logger)
    {
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) return;
        try
        {
            if (filePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Checking metadata for {File}", filePath);

                using var probe = new System.Diagnostics.Process();
                probe.StartInfo.FileName = GetFfprobePath();
                probe.StartInfo.Arguments = $"-v quiet -show_entries format_tags=title:stream_tags=title,handler_name -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"";
                probe.StartInfo.UseShellExecute = false;
                probe.StartInfo.RedirectStandardOutput = true;
                probe.Start();
                string titles = await probe.StandardOutput.ReadToEndAsync();
                await probe.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(titles))
                {
                    logger.LogInformation("File already sanitized or has no titles: {File}", filePath);
                    return;
                }

                bool needsSanitize = false;
                foreach (var line in titles.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = line.Trim();
                    if (t.Equals("VideoHandler", StringComparison.OrdinalIgnoreCase) || 
                        t.Equals("SoundHandler", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("SubtitleHandler", StringComparison.OrdinalIgnoreCase)) 
                    {
                        continue;
                    }
                    needsSanitize = true;
                    break;
                }

                if (!needsSanitize)
                {
                    logger.LogInformation("File already sanitized (no spam handlers): {File}", filePath);
                    return;
                }

                logger.LogInformation("Sanitizing metadata for {File}", filePath);
                string tmpFile = filePath + ".tmp";
                string format = filePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? "matroska" : "mp4";
                
                using var proc = new System.Diagnostics.Process();
                proc.StartInfo.FileName = GetFfmpegPath();
                // Strip all global metadata and stream-specific handlers/titles to completely purge embedded domains
                proc.StartInfo.Arguments = $"-y -i \"{filePath}\" -map 0 -c copy -map_metadata -1 -f {format} -metadata title=\"\" -metadata:s handler_name=\"\" -metadata:s title=\"\" \"{tmpFile}\"";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.Start();
                
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                await Task.WhenAll(outTask, errTask);
                await proc.WaitForExitAsync();
                
                if (proc.ExitCode == 0 && System.IO.File.Exists(tmpFile))
                {
                    System.IO.File.Delete(filePath);
                    System.IO.File.Move(tmpFile, filePath);
                    logger.LogInformation("Successfully sanitized metadata for {File}", filePath);
                }
                else
                {
                    string err = await errTask;
                    logger.LogWarning("ffmpeg metadata sanitization failed for {File}. ExitCode: {Code}. Error: {Error}", filePath, proc.ExitCode, err);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sanitize metadata for {File}", filePath);
        }
    }

    [HttpGet("MetadataStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetMetadataStatus()
    {
        List<string> logs;
        lock (_metadataLogLock)
        {
            logs = _metadataLogs.ToList();
        }
        return Ok(new
        {
            Status = _metadataStatus,
            Progress = _metadataProgress,
            Logs = logs
        });
    }

    [HttpPost("CleanMetadata")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult TriggerMetadataCleanup()
    {
        var worker = GetTaskWorker(_taskManager, "SanitizeScheduledTask", "SanitizeJellyFetchMetadata");
        if (worker != null)
        {
            if (TryExecuteScheduledTask(_taskManager, worker))
            {
                return Ok(new { Message = "Metadata sanitization task started via Jellyfin TaskManager." });
            }
            return StatusCode(500, new { Message = "Failed to start metadata sanitization task." });
        }

        // Fallback if TaskManager injection failed or task not registered
        if (_metadataStatus != "Idle" && _metadataStatus != "Completed" && _metadataStatus != "Error")
        {
            return BadRequest(new { Message = "Metadata sanitization is already running." });
        }

        _ = Task.Run(async () =>
        {
            await ExecuteMetadataCleanupAsync(_libraryManager, _logger, null, System.Threading.CancellationToken.None);
        });
        return Ok(new { Message = "Metadata cleanup started in background." });
    }

    public static async Task ExecuteMetadataCleanupAsync(
        ILibraryManager libraryManager,
        ILogger logger,
        IProgress<double>? taskProgress,
        System.Threading.CancellationToken ct)
    {
        try
        {
            ResetMetadataLogs();
            ReportMetadataProgress("Starting metadata sanitization...", 5);
            if (taskProgress != null) taskProgress.Report(5);
            
            var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Scan configured Downloads directory
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            string dir = config.DownloadsDirectory;
            if (string.IsNullOrEmpty(dir)) dir = "/media/Downloads";
            if (Directory.Exists(dir))
            {
                var localFiles = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
                foreach (var f in localFiles) allFiles.Add(f);
            }

            // 2. Scan entire Jellyfin library for Movies (in case they were moved by rclone/cron to other mounts)
            try
            {
                var movieItems = GetAllMovieItems(libraryManager, logger);
                foreach (var item in movieItems)
                {
                    if (!string.IsNullOrEmpty(item.Path) && 
                        (item.Path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || 
                         item.Path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
                    {
                        allFiles.Add(item.Path);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to query Jellyfin library for metadata cleanup.");
            }

            var files = allFiles.ToList();
            
            if (files.Count == 0)
            {
                ReportMetadataProgress("No MKV or MP4 files found to sanitize.", 100);
                if (taskProgress != null) taskProgress.Report(100);
                _metadataStatus = "Completed";
                return;
            }

            int count = 0;
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;
                double pct = 5 + ((double)count / files.Count * 85);
                ReportMetadataProgress($"Sanitizing {Path.GetFileName(file)}...", pct);
                if (taskProgress != null) taskProgress.Report(pct);
                
                await SanitizeVideoMetadataAsync(file, logger);
                count++;
            }

            ReportMetadataProgress("Refreshing Jellyfin Library...", 95);
            if (taskProgress != null) taskProgress.Report(95);
            await libraryManager.ValidateMediaLibrary(new Progress<double>(), ct);
            
            ReportMetadataProgress($"Successfully sanitized {count} files.", 100);
            if (taskProgress != null) taskProgress.Report(100);
            _metadataStatus = "Completed";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during library metadata sanitization.");
            ReportMetadataProgress("Error: " + ex.Message, 100);
            if (taskProgress != null) taskProgress.Report(100);
            _metadataStatus = "Error";
        }
    }

    public static async Task ExecuteCleanupAsync(
        ILibraryManager libraryManager,
        ILogger logger,
        IProgress<double>? taskProgress,
        System.Threading.CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var targetDir = config.DownloadsDirectory;
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
        {
            ReportCleanupProgress("Downloads directory does not exist or is not configured.", 0);
            return;
        }

        try
        {
            ResetCleanupLogs();
            ReportCleanupProgress("Starting cleanup in: " + targetDir, 5);
            taskProgress?.Report(5);

            // 1. Load currently allowed languages
            var allowedLangsPath = Path.Combine(Plugin.Instance.DataFolderPath, "allowed_languages.json");
            var allowedLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool languagesConfigured = false;
            if (System.IO.File.Exists(allowedLangsPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(allowedLangsPath);
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null)
                    {
                        languagesConfigured = true;
                        foreach (var l in list) allowedLangs.Add(l);
                    }
                }
                catch { }
            }

            string langStatusText = languagesConfigured
                ? (allowedLangs.Count > 0 ? string.Join(", ", allowedLangs) : "None (all languages deselected)")
                : "All";
            ReportCleanupProgress($"Active allowed languages: {langStatusText}", 10);
            taskProgress?.Report(10);

            // 2. Pre-scan library for real movies (to detect duplicates)
            using var client = new HttpClient();
            var scraper = new JellyfinScraper(client, libraryManager);
            var realLibrary = scraper.LoadRealLibraryMedia(targetDir, (msg, _) => AddCleanupLog(msg));
            ReportCleanupProgress($"Library check: {realLibrary.Keys.Count} existing downloaded movies identified.", 15);
            taskProgress?.Report(15);

            // 3. Scan subdirectories in targetDir
            var subDirs = new List<string>(Directory.GetDirectories(targetDir));
            
            // Add all other root directories that contain .strm files (for global deduplication)
            var configDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetDir };
            string[] fallbackDirs = { "/media", "/media/Downloads", "/mnt/msp/Movies" };
            foreach (var fb in fallbackDirs)
            {
                if (Directory.Exists(fb) && !configDirs.Contains(fb))
                {
                    configDirs.Add(fb);
                    try { subDirs.AddRange(Directory.GetDirectories(fb)); } catch { }
                }
            }
            
            // Deduplicate subDirs
            subDirs = subDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            int total = subDirs.Count;
            int deletedCount = 0;
            int keptCount = 0;
            ReportCleanupProgress($"Found {total} folders in downloads directory to inspect.", 15);
            
            // Track seen .strm movies for cross-directory duplicate deletion
            var seenStrmMovies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested) break;
                var dir = subDirs[i];
                var dirName = Path.GetFileName(dir);
                var pct = 15.0 + ((double)(i + 1) / (total == 0 ? 1 : total) * 75.0);
                var roundedPct = Math.Round(pct, 1);
                ReportCleanupProgress($"Checking {i + 1}/{total}: {dirName}", roundedPct);
                taskProgress?.Report(roundedPct);

                try
                {
                    // Check if folder contains real downloaded video files (.mkv, .mp4, .avi)
                    bool hasRealVideo = Directory.GetFiles(dir).Any(f => 
                        f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || 
                        f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || 
                        f.EndsWith(".avi", StringComparison.OrdinalIgnoreCase));

                    if (hasRealVideo)
                    {
                        keptCount++;
                        continue;
                    }

                    // Check if movie is blocklisted
                    var (dFull, dBase, dYear) = JellyfinScraper.CleanMovieTitle(dirName);
                    string? diskTmdbId = null;
                    var nfo = Path.Combine(dir, "movie.nfo");
                    if (System.IO.File.Exists(nfo))
                    {
                        try
                        {
                            var nfoTxt = System.IO.File.ReadAllText(nfo);
                            var tm = System.Text.RegularExpressions.Regex.Match(nfoTxt, @"<tmdbid>(\d+)</tmdbid>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (tm.Success) diskTmdbId = tm.Groups[1].Value;
                        }
                        catch { }
                    }
                    var blocklist = Plugin.Instance?.Configuration?.Blocklist ?? Array.Empty<string>();
                    bool isBlocklisted = JellyfinScraper.IsBlocklisted(dFull, dBase, diskTmdbId, blocklist);

                    // Check if this is a duplicate of a movie already in the library
                    var normKey = JellyfinScraper.NormalizeKey(dirName);
                    bool isDuplicateOfLibrary = !string.IsNullOrEmpty(normKey) && realLibrary.Keys.Contains(normKey);

                    // Cross-check TMDB / IMDb IDs in movie.nfo against library
                    if (!isDuplicateOfLibrary)
                    {
                        if (!string.IsNullOrEmpty(diskTmdbId) && realLibrary.TmdbIds.Contains(diskTmdbId))
                        {
                            isDuplicateOfLibrary = true;
                        }
                        else if (System.IO.File.Exists(nfo))
                        {
                            try
                            {
                                var nfoTxt = System.IO.File.ReadAllText(nfo);
                                var im = System.Text.RegularExpressions.Regex.Match(nfoTxt, @"<imdbid>(tt\d+)</imdbid>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (im.Success && realLibrary.ImdbIds.Contains(im.Groups[1].Value)) isDuplicateOfLibrary = true;
                            }
                            catch { }
                        }
                    }

                    // Cross-directory duplicate check
                    if (!isDuplicateOfLibrary && !string.IsNullOrEmpty(normKey))
                    {
                        if (seenStrmMovies.Contains(normKey))
                        {
                            isDuplicateOfLibrary = true;
                        }
                        else
                        {
                            seenStrmMovies.Add(normKey);
                        }
                    }

                    // Check language filter against downloads.json
                    bool isDeselectedLanguage = false;
                    string jsonPath = Path.Combine(dir, "downloads.json");
                    List<string> movieLangs = new();
                    if (System.IO.File.Exists(jsonPath) && languagesConfigured)
                    {
                        try
                        {
                            var jContent = System.IO.File.ReadAllText(jsonPath);
                            var options = JsonSerializer.Deserialize<List<ScrapedMagnetOption>>(jContent);
                            if (options != null)
                            {
                                foreach (var opt in options)
                                {
                                    if (opt.languages != null) movieLangs.AddRange(opt.languages);
                                }
                                if (movieLangs.Count > 0)
                                {
                                    if (!movieLangs.Any(l => allowedLangs.Contains(l)))
                                    {
                                        isDeselectedLanguage = true;
                                    }
                                }
                                else if (allowedLangs.Count == 0)
                                {
                                    isDeselectedLanguage = true;
                                }
                            }
                        }
                        catch { }
                    }

                    // CRITICAL FIX: Also verify the movie's actual original language!
                    // Dubbed releases in downloads.json might only record ["Tamil"], but their original
                    // language is Kannada/Telugu/Hindi. If the original language is deselected, flag it for deletion!
                    if (!isDeselectedLanguage && languagesConfigured)
                    {
                        try
                        {
                            var (detectLangs, ok, tmdbId, imdbId, origLangs) = await scraper.DetectLanguageAsync(dirName, "", allowedLangs, dBase, dYear);
                            if (!ok)
                            {
                                isDeselectedLanguage = true;
                                if (origLangs != null && origLangs.Count > 0)
                                {
                                    movieLangs = origLangs;
                                }
                            }
                        }
                        catch { }
                    }

                    bool hasStrm = Directory.GetFiles(dir, "*.strm").Any();

                    void DeleteLibraryItemIfExists(string dirPath)
                    {
                        try
                        {
                            if (libraryManager != null && Directory.Exists(dirPath))
                            {
                                foreach (var strmFile in Directory.GetFiles(dirPath, "*.strm", SearchOption.AllDirectories))
                                {
                                    try
                                    {
                                        var item = libraryManager.FindByPath(strmFile, false);
                                        if (item != null)
                                        {
                                            libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }

                    if (isBlocklisted)
                    {
                        DeleteLibraryItemIfExists(dir);
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                        deletedCount++;
                        AddCleanupLog($"[Deleted Blocklisted] {dirName}");
                        logger.LogInformation("[JellyFetch Cleanup] Deleted blocklisted movie: {Dir}", dirName);
                    }
                    else if (isDuplicateOfLibrary)
                    {
                        DeleteLibraryItemIfExists(dir);
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                        deletedCount++;
                        AddCleanupLog($"[Deleted Duplicate] {dirName} (Already downloaded in library)");
                        logger.LogInformation("[JellyFetch Cleanup] Deleted duplicate: {Dir}", dirName);
                    }
                    else if (isDeselectedLanguage)
                    {
                        DeleteLibraryItemIfExists(dir);
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                        deletedCount++;
                        string langListStr = movieLangs.Count > 0 ? string.Join(", ", movieLangs.Distinct()) : "unselected language";
                        AddCleanupLog($"[Deleted Language] {dirName} (Deselected: {langListStr})");
                        logger.LogInformation("[JellyFetch Cleanup] Deleted deselected language movie: {Dir} ({Langs})", dirName, langListStr);
                    }
                    else if (!hasStrm)
                    {
                        DeleteLibraryItemIfExists(dir);
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                        deletedCount++;
                        AddCleanupLog($"[Deleted Orphan] {dirName}");
                    }
                    else
                    {
                        keptCount++;
                    }
                }
                catch (Exception ex)
                {
                    AddCleanupLog($"[Error] Could not remove {dirName}: {ex.Message}");
                }
            }

            // 4. Refresh Jellyfin library if items were removed
            if (deletedCount > 0)
            {
                ReportCleanupProgress($"Refreshing Jellyfin library ({deletedCount} movies removed)...", 92);
                taskProgress?.Report(92);
                AddCleanupLog("Triggering Jellyfin library scan to update UI...");
                await libraryManager.ValidateMediaLibrary(new Progress<double>(), System.Threading.CancellationToken.None);
            }

            ReportCleanupProgress($"Cleanup completed. Removed {deletedCount} folders. Kept {keptCount} movies.", 100);
            taskProgress?.Report(100);
            AddCleanupLog($"Done! Removed {deletedCount} folders. Kept {keptCount} movies.");
        }
        catch (OperationCanceledException)
        {
            ReportCleanupProgress("Cleanup stopped by user.", 0);
        }
        catch (Exception ex)
        {
            ReportCleanupProgress("Error during cleanup: " + ex.Message, 0);
            logger.LogError(ex, "Cleanup error");
        }
    }

    [HttpPost("CleanupStrm")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult CleanupStrm()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var targetDir = config.DownloadsDirectory;
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
        {
            return BadRequest(new { Message = "Downloads directory does not exist or is not configured." });
        }

        var worker = GetTaskWorker(_taskManager, "CleanupScheduledTask", "CleanJellyFetchStrm");
        if (worker != null)
        {
            if (worker.State == TaskState.Running)
            {
                return BadRequest(new { Message = "Cleanup is already running." });
            }

            _cleanupProgress = 1;
            _cleanupStartTime = DateTime.UtcNow;
            _cleanupStatus = "Starting cleanup...";
            ResetCleanupLogs();
            AddCleanupLog("Starting cleanup in: " + targetDir);

            if (TryExecuteScheduledTask(_taskManager, worker))
            {
                return Ok(new { Message = "Cleanup started." });
            }
        }

        if (_cleanupCts != null && !_cleanupCts.IsCancellationRequested)
        {
            return BadRequest(new { Message = "Cleanup is already running." });
        }

        _cleanupCts = new System.Threading.CancellationTokenSource();
        _cleanupProgress = 1;
        _cleanupStartTime = DateTime.UtcNow;
        _cleanupStatus = "Initializing cleanup...";
        ResetCleanupLogs();
        AddCleanupLog("Starting cleanup in: " + targetDir);

        _ = Task.Run(async () =>
        {
            await ExecuteCleanupAsync(_libraryManager, _logger, null, _cleanupCts.Token);
            var currentCts = _cleanupCts;
            _ = Task.Delay(15000).ContinueWith(t =>
            {
                if (_cleanupCts == currentCts)
                {
                    _cleanupCts = null;
                    if (_cleanupProgress == 100)
                    {
                        _cleanupStatus = "Idle";
                        _cleanupProgress = 0;
                    }
                }
            });
        });

        return Ok(new { Message = "Cleanup started." });
    }

    [HttpPost("ResetPlugin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetPlugin()
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.UpdateConfiguration(new PluginConfiguration());
            
            var pluginDataPath = Plugin.Instance.DataFolderPath;
            var languagesPath = Path.Combine(pluginDataPath, "allowed_languages.json");
            if (System.IO.File.Exists(languagesPath))
            {
                try
                {
                    System.IO.File.Delete(languagesPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting allowed_languages.json during reset.");
                }
            }
        }
        return Ok(new { Message = "Plugin configuration has been reset to defaults." });
    }

    [HttpGet("CleanupStrm/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetCleanupStatus()
    {
        bool isRunning = false;
        var worker = GetTaskWorker(_taskManager, "CleanupScheduledTask", "CleanJellyFetchStrm");
        if (worker != null)
        {
            isRunning = worker.State == TaskState.Running;
            if (isRunning && _cleanupProgress <= 1 && worker.CurrentProgress.HasValue && worker.CurrentProgress.Value > 0)
            {
                _cleanupProgress = Math.Round(worker.CurrentProgress.Value, 1);
            }
        }
        if (!isRunning)
        {
            isRunning = _cleanupCts != null && !_cleanupCts.IsCancellationRequested;
        }
        if (!isRunning && (DateTime.UtcNow - _cleanupStartTime).TotalSeconds < 10 && _cleanupStatus.StartsWith("Starting", StringComparison.OrdinalIgnoreCase))
        {
            isRunning = true;
        }

        List<string> logsCopy;
        lock (_cleanupLogLock)
        {
            logsCopy = new List<string>(_cleanupLogs);
        }
        return Ok(new
        {
            IsRunning = isRunning,
            Progress = _cleanupProgress,
            Status = _cleanupStatus,
            Logs = logsCopy
        });
    }

    [HttpDelete("CleanupStrm")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult StopCleanup()
    {
        var worker = GetTaskWorker(_taskManager, "CleanupScheduledTask", "CleanJellyFetchStrm");
        if (worker != null)
        {
            TryCancelScheduledTask(_taskManager, worker);
        }
        if (_cleanupCts != null && !_cleanupCts.IsCancellationRequested)
        {
            _cleanupCts.Cancel();
        }
        _cleanupStatus = "Cleanup stopped by user.";
        AddCleanupLog("Cleanup stopped by user.");
        return Ok(new { Message = "Cleanup stopped." });
    }

    private static List<BaseItem> GetAllMovieItems(ILibraryManager? libraryManager, ILogger? logger)
    {
        var result = new List<BaseItem>();
        if (libraryManager == null) return result;

        try
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false
            };
            var method = libraryManager.GetType().GetMethod("GetItemList", new[] { typeof(InternalItemsQuery) }) 
                         ?? typeof(ILibraryManager).GetMethod("GetItemList", new[] { typeof(InternalItemsQuery) });
            if (method != null)
            {
                var items = (System.Collections.IEnumerable?)method.Invoke(libraryManager, new object[] { query });
                if (items != null)
                {
                    foreach (BaseItem item in items)
                    {
                        if (item != null)
                        {
                            result.Add(item);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to query Jellyfin library via GetAllMovieItems.");
        }

        return result;
    }

    [HttpDelete("PurgeAllStrm")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult PurgeAllStrm()
    {
        var config = Plugin.Instance?.Configuration;
        string? targetDir = config?.DownloadsDirectory;
        if (string.IsNullOrEmpty(targetDir)) targetDir = "/media/Downloads";

        try
        {
            int count = 0;
            var deletedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Direct SQLite Item Purge: Query ILibraryManager for all Movie items whose path ends with .strm
            if (_libraryManager != null)
            {
                var movieItems = GetAllMovieItems(_libraryManager, _logger);
                foreach (var item in movieItems)
                {
                    if (item != null && !string.IsNullOrEmpty(item.Path) && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string itemDir = Path.GetDirectoryName(item.Path) ?? "";
                            bool hasRealVideo = false;
                            if (!string.IsNullOrEmpty(itemDir) && Directory.Exists(itemDir))
                            {
                                hasRealVideo = Directory.GetFiles(itemDir, "*.*", SearchOption.AllDirectories).Any(f =>
                                    f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".avi", StringComparison.OrdinalIgnoreCase));
                            }

                            // Atomically remove item from SQLite database
                            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = System.IO.File.Exists(item.Path) }, true);
                            count++;

                            if (!string.IsNullOrEmpty(itemDir) && !hasRealVideo && Directory.Exists(itemDir))
                            {
                                try { Directory.Delete(itemDir, true); } catch { }
                                deletedDirs.Add(itemDir);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to atomically delete movie item {Path} during purge", item.Path);
                        }
                    }
                }
            }

            // 2. Filesystem Sweep: Clean any remaining .strm folders or sidecars on disk
            if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
            {
                var dirs = Directory.GetDirectories(targetDir);
                foreach (var dir in dirs)
                {
                    if (deletedDirs.Contains(dir)) continue;

                    bool hasRealVideo = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories).Any(f => 
                        f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || 
                        f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || 
                        f.EndsWith(".avi", StringComparison.OrdinalIgnoreCase));

                    if (hasRealVideo)
                    {
                        foreach (var strm in Directory.GetFiles(dir, "*.strm", SearchOption.AllDirectories))
                        {
                            try { System.IO.File.Delete(strm); } catch { }
                        }
                        continue;
                    }

                    if (Directory.GetFiles(dir, "*.strm", SearchOption.AllDirectories).Any() ||
                        System.IO.File.Exists(Path.Combine(dir, "downloads.json")))
                    {
                        try
                        {
                            if (_libraryManager != null)
                            {
                                foreach (var strmFile in Directory.GetFiles(dir, "*.strm", SearchOption.AllDirectories))
                                {
                                    try
                                    {
                                        var item = _libraryManager.FindByPath(strmFile, false);
                                        if (item != null)
                                        {
                                            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
                                        }
                                    }
                                    catch { }
                                }
                            }

                            if (Directory.Exists(dir))
                            {
                                Directory.Delete(dir, true);
                            }
                            count++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to delete directory: {dir}");
                        }
                    }
                }
            }

            if (count > 0 && _libraryManager != null)
            {
                // Trigger a library scan to remove phantom items from the UI
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    try { await _libraryManager.ValidateMediaLibrary(new Progress<double>(), System.Threading.CancellationToken.None); } catch { }
                });
            }

            return Ok(new { Message = $"Purged {count} movie items/folders containing .strm files." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error purging all scraped movies");
            return StatusCode(500, new { Message = "Error purging files: " + ex.Message });
        }
    }

    [HttpPost("Scrape")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Scrape()
    {
        var worker = GetTaskWorker(_taskManager, "ScraperScheduledTask", "ExternalMediaScraper");
        if (worker != null)
        {
            if (worker.State == TaskState.Running)
            {
                return BadRequest(new { Message = "Scraping is already running." });
            }

            _scrapeStatus = "Starting scraper...";
            _scrapeProgress = 0;
            ResetScrapeLogs();
            AddScrapeLog("Starting scraper task via Jellyfin TaskManager...");

            if (TryExecuteScheduledTask(_taskManager, worker))
            {
                return Ok(new { Message = "Scraping started." });
            }
        }

        lock (_scrapeLogLock)
        {
            if (_scrapeCts != null && !_scrapeCts.IsCancellationRequested)
            {
                return BadRequest(new { Message = "Scraping is already running." });
            }
            _scrapeCts = new System.Threading.CancellationTokenSource();
            _scrapeStatus = "Starting scraper...";
            _scrapeProgress = 0;
            _scrapeLogs.Clear();
            _scrapeLogs.Add($"[{DateTime.Now:HH:mm:ss}] Scraper started directly.");
        }
        
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient();
                var scraper = new JellyfinScraper(client, _libraryManager);
                var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
                
                var res = await scraper.RunScrapeAsync(config.DownloadsDirectory, (msg, pct) => 
                {
                    ReportScrapeProgress(msg, pct);
                    _logger.LogInformation("Scraper: {Msg}", msg);
                }, _scrapeCts.Token);
                
                if (res.Success)
                {
                    ReportScrapeProgress("Scanning Jellyfin media library...", 95);
                    await _libraryManager.ValidateMediaLibrary(new Progress<double>(), System.Threading.CancellationToken.None);
                    ReportScrapeProgress($"Completed. Found {res.NewMoviesCount} new movies.", 100);
                }
                else
                {
                    ReportScrapeProgress($"Failed: {res.Error}", 0);
                }
            }
            catch (OperationCanceledException)
            {
                ReportScrapeProgress("Scraper stopped by user.", 0);
            }
            catch (Exception ex)
            {
                ReportScrapeProgress("Error: " + ex.Message, 0);
                _logger.LogError(ex, "Scraper error");
            }
            finally
            {
                var currentCts = _scrapeCts;
                _ = Task.Delay(15000).ContinueWith(t => 
                {
                    if (currentCts != null && currentCts.IsCancellationRequested)
                    {
                        _scrapeStatus = "Idle";
                    }
                    else if (_scrapeProgress == 100)
                    {
                        _scrapeStatus = "Idle";
                        _scrapeProgress = 0;
                    }
                    
                    if (_scrapeCts == currentCts)
                    {
                        _scrapeCts = null;
                    }
                });
            }
        });
        return Ok(new { Message = "Scraping started." });
    }

    [HttpPost("TestCredentials")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> TestCredentials([FromBody] TestCredentialsRequest req)
    {
        var errors = new List<string>();
        using var client = new HttpClient();
        
        if (!string.IsNullOrEmpty(req.SeedrUsername) && !string.IsNullOrEmpty(req.SeedrPassword))
        {
            var seedr = new SeedrHelper(client);
            var token = await seedr.LoginAsync(req.SeedrUsername, req.SeedrPassword);
            if (string.IsNullOrEmpty(token))
            {
                errors.Add("Seedr login failed. Check username and password.");
            }
        }
        
        if (!string.IsNullOrEmpty(req.TorboxApiKey))
        {
            var reqMsg = new HttpRequestMessage(HttpMethod.Get, "https://api.torbox.app/v1/api/user/me");
            reqMsg.Headers.Add("Authorization", "Bearer " + req.TorboxApiKey);
            try
            {
                var res = await client.SendAsync(reqMsg);
                if (!res.IsSuccessStatusCode)
                {
                    errors.Add("Torbox API key validation failed (Status " + res.StatusCode + ").");
                }
            }
            catch
            {
                errors.Add("Could not reach Torbox servers to validate API key.");
            }
        }
        
        if (errors.Count > 0)
        {
            return BadRequest(new { Message = string.Join(" ", errors) });
        }
        
        return Ok(new { Message = "Credentials are valid." });
    }

    [HttpGet("Scrape/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetScrapeStatus()
    {
        bool isRunning = false;
        var worker = GetTaskWorker(_taskManager, "ScraperScheduledTask", "ExternalMediaScraper");
        if (worker != null)
        {
            isRunning = worker.State == TaskState.Running;
            if (isRunning && _scrapeProgress == 0 && worker.CurrentProgress.HasValue && worker.CurrentProgress.Value > 0)
            {
                _scrapeProgress = Math.Round(worker.CurrentProgress.Value, 1);
            }
        }
        if (!isRunning)
        {
            isRunning = _scrapeCts != null && !_scrapeCts.IsCancellationRequested;
        }

        List<string> logsCopy;
        lock (_scrapeLogLock)
        {
            logsCopy = new List<string>(_scrapeLogs);
        }
        return Ok(new { 
            IsRunning = isRunning,
            Progress = _scrapeProgress,
            Status = _scrapeStatus,
            Logs = logsCopy
        });
    }

    [HttpDelete("Scrape")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult StopScrape()
    {
        var worker = GetTaskWorker(_taskManager, "ScraperScheduledTask", "ExternalMediaScraper");
        if (worker != null)
        {
            TryCancelScheduledTask(_taskManager, worker);
        }
        if (_scrapeCts != null && !_scrapeCts.IsCancellationRequested)
        {
            _scrapeCts.Cancel();
        }
        ReportScrapeProgress("Scraper stopped by user.", 0);
        return Ok(new { Message = "Scraper stopped." });
    }

    [HttpGet("Active")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> GetActiveDownloads()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var activeList = new List<object>();
        using var client = new HttpClient();

        if (!string.IsNullOrEmpty(config.TorboxApiKey))
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://api.torbox.app/v1/api/torrents/mylist");
                req.Headers.Add("Authorization", $"Bearer {config.TorboxApiKey}");
                var resp = await client.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var content = await resp.Content.ReadAsStringAsync();
                    var data = JsonDocument.Parse(content).RootElement.GetProperty("data");
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("download_state", out var state) && state.GetString() != "cached")
                        {
                            activeList.Add(new
                            {
                                Name = item.GetProperty("name").GetString(),
                                Provider = "Torbox",
                                Progress = item.GetProperty("progress").GetDouble() * 100.0,
                                State = state.GetString()
                            });
                        }
                    }
                }
            }
            catch { }
        }
        return Ok(activeList);
    }

    private static async Task StreamWithProgressAsync(
        string url, 
        string targetPath, 
        DownloadProgressInfo progress, 
        System.Threading.CancellationToken cancellationToken = default)
    {
        var tempPath = targetPath + ".part";
        long totalRead = 0;
        
        using var streamClient = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        streamClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36");

        if (System.IO.File.Exists(tempPath))
        {
            try { totalRead = new FileInfo(tempPath).Length; } catch { totalRead = 0; }
        }

        long totalBytes = -1L;
        var lastReport = DateTime.UtcNow;
        long lastBytes = totalRead;

        bool done = false;
        int retries = 0;
        while (!done && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (totalRead > 0)
                {
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(totalRead, null);
                }

                using var connCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connCts.CancelAfter(TimeSpan.FromSeconds(30));
                
                using var response = await streamClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connCts.Token);
                response.EnsureSuccessStatusCode();

                if (totalBytes == -1L)
                {
                    totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    if (totalRead > 0 && totalBytes != -1L)
                    {
                        totalBytes += totalRead;
                    }
                    if (totalBytes > 0 && progress.SizeGb <= 0)
                    {
                        progress.SizeGb = Math.Round((double)totalBytes / (1024.0 * 1024.0 * 1024.0), 2);
                    }
                }

                using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(tempPath, totalRead > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1024 * 1024, true);

                var buffer = new byte[1024 * 512];
                int bytesRead;

                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    if (progress.IsPaused)
                    {
                        progress.PauseEvent.Wait(cancellationToken);
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalRead += bytesRead;

                    var now = DateTime.UtcNow;
                    var elapsed = (now - lastReport).TotalSeconds;
                    if (elapsed >= 0.7 && !progress.IsPaused)
                    {
                        var deltaBytes = totalRead - lastBytes;
                        var speedMbps = (deltaBytes * 8.0) / (elapsed * 1024.0 * 1024.0);
                        lastBytes = totalRead;
                        lastReport = now;

                        double readMb = totalRead / (1024.0 * 1024.0);
                        if (totalBytes > 0)
                        {
                            double totalMb = totalBytes / (1024.0 * 1024.0);
                            double pct = (double)totalRead / totalBytes;
                            progress.Progress = Math.Round(30.0 + (pct * 62.0), 1);
                            progress.Status = $"Downloading: {readMb:0.0} MB / {totalMb:0.0} MB ({speedMbps:0.0} Mbps)";
                        }
                        else
                        {
                            progress.Status = $"Downloading: {readMb:0.0} MB ({speedMbps:0.0} Mbps)";
                        }
                    }
                }

                done = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested) throw;

                retries++;
                if (retries > 5) 
                {
                    progress.Error = ex.Message;
                    throw;
                }

                if (progress.IsPaused)
                {
                    progress.PauseEvent.Wait(cancellationToken);
                }
                else
                {
                    await Task.Delay(2000, cancellationToken);
                }
            }
        }

        if (System.IO.File.Exists(targetPath))
        {
            try { System.IO.File.Delete(targetPath); } catch { }
        }
        System.IO.File.Move(tempPath, targetPath);
        if (System.IO.File.Exists(targetPath))
        {
            try
            {
                var fi = new FileInfo(targetPath);
                if (fi.Length > 0)
                {
                    progress.SizeGb = Math.Round((double)fi.Length / (1024.0 * 1024.0 * 1024.0), 2);
                }
            }
            catch { }
        }
    }

    public class ManualDownloadRequest
    {
        public string MagnetUri { get; set; } = string.Empty;
        public string Provider { get; set; } = "auto";
    }

    [HttpPost("ManualDownload")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult StartManualDownload([FromBody] ManualDownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.MagnetUri))
        {
            return BadRequest("Magnet URI or Direct Link is required.");
        }
        bool isHttp = request.MagnetUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                      request.MagnetUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var config = Plugin.Instance.Configuration;
        string fakeItemId = "manual-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string movieName = "Manual Download";
        var dnMatch = System.Text.RegularExpressions.Regex.Match(request.MagnetUri, @"[?&]dn=([^&]+)");
        if (dnMatch.Success)
        {
            try
            {
                var rawDn = Uri.UnescapeDataString(dnMatch.Groups[1].Value.Replace('+', ' '));
                if (!string.IsNullOrWhiteSpace(rawDn))
                {
                    movieName = CleanMediaTitle(rawDn);
                }
            }
            catch { }
        }

        var cts = new System.Threading.CancellationTokenSource();
        
        string providerChoice = request.Provider?.ToLowerInvariant() ?? "auto";
        double fakeSize = 0.0; // Size dynamically detected by cloud providers
        var xlMatch = System.Text.RegularExpressions.Regex.Match(request.MagnetUri, @"[?&]xl=([0-9]+)");
        if (xlMatch.Success && long.TryParse(xlMatch.Groups[1].Value, out long xlBytes) && xlBytes > 0)
        {
            fakeSize = Math.Round((double)xlBytes / (1024.0 * 1024.0 * 1024.0), 2);
        }

        var info = new DownloadProgressInfo
        {
            ItemId = fakeItemId,
            MovieName = movieName,
            SizeGb = fakeSize,
            Status = "Queued",
            Provider = "Unknown",
            Timestamp = DateTime.UtcNow,
            Cts = cts,
            MagnetUri = request.MagnetUri,
            ItemPath = ""
        };
        _activeDownloads[fakeItemId] = info;

        if (isHttp)
        {
            info.Provider = "DirectLink";
            _ = ProcessDirectDownload(info, config, request.MagnetUri);
            return Ok(new { Success = true, Provider = "DirectLink" });
        }
        else if (providerChoice == "seedr" || (providerChoice == "auto" && config.EnableSeedr && !config.EnableTorbox))
        {
            info.Provider = "Seedr";
            _ = ProcessSeedrDownload(info, config, "", request.MagnetUri, fakeSize, null, null);
            return Ok(new { Success = true, Provider = "Seedr" });
        }
        else if (providerChoice == "torbox" || (providerChoice == "auto" && config.EnableTorbox))
        {
            info.Provider = "Torbox";
            _ = ProcessTorboxDownload(info, config, "", request.MagnetUri, fakeSize, null, null);
            return Ok(new { Success = true, Provider = "Torbox" });
        }
        else
        {
            info.Status = "Failed";
            info.Error = "No eligible download provider enabled or selected.";
            info.Completed = true;
            return BadRequest(new { Success = false, ErrorMessage = info.Error });
        }
    }

    private async Task ProcessDirectDownload(DownloadProgressInfo progress, PluginConfiguration config, string fileUrl)
    {
        try
        {
            progress.Status = "Starting direct download...";
            progress.Progress = 10;
            
            string targetDir = config.DownloadsDirectory;
            if (string.IsNullOrEmpty(targetDir)) targetDir = "/media/Downloads";
            if (!Directory.Exists(targetDir)) {
                try { Directory.CreateDirectory(targetDir); } catch { }
            }
            
            string fileName = "ManualDownload_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4";
            try 
            {
                var uri = new Uri(fileUrl);
                var fn = Path.GetFileName(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(fn) && fn.Contains(".")) fileName = Uri.UnescapeDataString(fn);
            } catch { }

            fileName = CleanMediaFileName(fileName);
            string targetPath = Path.Combine(targetDir, fileName);
            progress.TargetPath = targetPath;
            progress.MovieName = Path.GetFileNameWithoutExtension(fileName);
            
            await StreamWithProgressAsync(fileUrl, targetPath, progress, progress.Cts.Token);
            
            progress.Progress = 100;
            progress.Status = "Completed";
            progress.Completed = true;
            
            await SanitizeVideoMetadataAsync(targetPath, _logger);
            
            _ = Task.Run(async () => {
                try { await _libraryManager.ValidateMediaLibrary(new Progress<double>(), System.Threading.CancellationToken.None); } catch { }
            });

            double finalSize = progress.SizeGb;
            if (finalSize <= 0 && System.IO.File.Exists(targetPath))
            {
                try { finalSize = Math.Round((double)new FileInfo(targetPath).Length / (1024.0 * 1024.0 * 1024.0), 2); progress.SizeGb = finalSize; } catch { }
            }
            _ = DownloadHistoryManager.AddEntryAsync(new DownloadHistoryEntry { MovieName = progress.MovieName, SizeGb = finalSize, Provider = "DirectLink", Status = "Completed" });
            _ = Task.Delay(15000).ContinueWith(t => _activeDownloads.TryRemove(progress.ItemId, out var ignored));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Direct download cancelled for {ItemId}", progress.ItemId);
            progress.Status = "Stopped";
            progress.Completed = true;
            _ = DownloadHistoryManager.AddEntryAsync(new DownloadHistoryEntry { MovieName = progress.MovieName, SizeGb = progress.SizeGb, Provider = "DirectLink", Status = "Stopped" });
            if (!string.IsNullOrEmpty(progress.TargetPath))
            {
                if (System.IO.File.Exists(progress.TargetPath))
                {
                    try { System.IO.File.Delete(progress.TargetPath); } catch { }
                }
                var partPath = progress.TargetPath + ".part";
                if (System.IO.File.Exists(partPath))
                {
                    try { System.IO.File.Delete(partPath); } catch { }
                }
            }
            _activeDownloads.TryRemove(progress.ItemId, out _);
        }
        catch (Exception ex)
        {
            progress.Status = "Failed";
            progress.Error = ex.Message;
            progress.Completed = true;
            _ = DownloadHistoryManager.AddEntryAsync(new DownloadHistoryEntry { MovieName = progress.MovieName, SizeGb = progress.SizeGb, Provider = "DirectLink", Status = "Failed", ErrorMessage = ex.Message });
            if (!string.IsNullOrEmpty(progress.TargetPath))
            {
                var partPath = progress.TargetPath + ".part";
                if (System.IO.File.Exists(partPath))
                {
                    try { System.IO.File.Delete(partPath); } catch { }
                }
            }
            _activeDownloads.TryRemove(progress.ItemId, out _);
        }
    }

    private static string CleanMediaTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Manual Download";

        string name = raw.Trim();
        string ext = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(ext) && (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || 
                                           ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || 
                                           ext.Equals(".avi", StringComparison.OrdinalIgnoreCase) || 
                                           ext.Equals(".mov", StringComparison.OrdinalIgnoreCase) || 
                                           ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        name = System.Text.RegularExpressions.Regex.Replace(
            name,
            @"^(?:https?:\/\/)?(?:www\.)?[a-zA-Z0-9.-]+\.[a-zA-Z]{2,6}\s*[-–_:]\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        name = System.Text.RegularExpressions.Regex.Replace(
            name,
            @"^\[?[a-zA-Z0-9.-]*1[tT]amil[mM][vV][a-zA-Z0-9.-]*\]?\s*[-–_:]?\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return string.IsNullOrWhiteSpace(name) ? raw.Trim() : name.Trim();
    }

    private static string CleanMediaFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fileName;

        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            fileName,
            @"^(?:www\.)?1[tT]amil[mM][vV]\.[a-zA-Z0-9]+\s*[-–]\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned,
            @"^(?:www\.)?[a-zA-Z0-9.-]+\.[a-zA-Z]{2,6}\s*[-–]\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return string.IsNullOrWhiteSpace(cleaned) ? fileName : cleaned.Trim();
    }
}
