using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.JellyFetch.Helpers
{
    public class ScrapeResult
    {
        public bool Success { get; set; }
        public int NewMoviesCount { get; set; }
        public string Error { get; set; }
    }

    public class ScrapedMagnetOption
    {
        public string uri { get; set; } = string.Empty;
        public string dn { get; set; } = string.Empty;
        public double xl_gb { get; set; }
        public List<string> languages { get; set; } = new List<string>();
    }

    public class JellyfinScraper
    {
        private readonly HttpClient _httpClient;
        private readonly ILibraryManager _libraryManager;
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _movieLocks = new(StringComparer.OrdinalIgnoreCase);

        public JellyfinScraper(HttpClient httpClient, ILibraryManager libraryManager = null)
        {
            _httpClient = httpClient;
            _libraryManager = libraryManager;
        }

        private string GetCacheFile()
        {
            return Path.Combine(Plugin.Instance.DataFolderPath, ".last_domain");
        }

        private HashSet<string> LoadAllowedLanguages()
        {
            string p = Path.Combine(Plugin.Instance.DataFolderPath, "allowed_languages.json");
            if (File.Exists(p))
            {
                try
                {
                    var json = File.ReadAllText(p);
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null) return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                }
                catch { }
            }
            return new HashSet<string>(new[] { "Tamil", "Malayalam", "Hindi", "Telugu", "Kannada", "English" }, StringComparer.OrdinalIgnoreCase);
        }

        private async Task<string> GetWorkingDomainAsync(CancellationToken ct)
        {
            var domains = new List<string> { "1tamilmv.meme", "1tamilmv.rocks", "1tamilmv.ing", "1tamilmv.xyz", "1tamilmv.pizza", "1tamilmv.pics", "1tamilmv.eu", "1tamilmv.tf" };
            
            // Priority 1: User's manual backup override (if configured)
            var overrideFile = Path.Combine(Plugin.Instance.DataFolderPath, ".custom_domain");
            bool hasOverride = false;
            if (File.Exists(overrideFile))
            {
                try
                {
                    var custom = (await File.ReadAllTextAsync(overrideFile, ct)).Trim();
                    if (!string.IsNullOrEmpty(custom))
                    {
                        domains.Remove(custom);
                        domains.Insert(0, custom);
                        hasOverride = true;
                    }
                }
                catch { }
            }

            // Priority 2: Last auto-discovered working domain
            var cacheFile = GetCacheFile();
            if (File.Exists(cacheFile))
            {
                try
                {
                    var cached = (await File.ReadAllTextAsync(cacheFile, ct)).Trim();
                    if (!string.IsNullOrEmpty(cached))
                    {
                        domains.Remove(cached);
                        int insertIdx = hasOverride ? 1 : 0;
                        if (insertIdx < domains.Count) domains.Insert(insertIdx, cached);
                        else domains.Add(cached);
                    }
                }
                catch { }
            }

            foreach (var d in domains)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.{d}/index.php?/forums/");
                    req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                            await File.WriteAllTextAsync(cacheFile, d, ct);
                        }
                        catch { }
                        return d;
                    }
                }
                catch { }
            }
            return null;
        }

        private bool IsExcluded(string title)
        {
            string t = title.ToLowerInvariant();
            string[] patterns = { @"s\d{2}e\d{2}", @"\bseason\s?\d+\b", @"\bep\s?\d+\b", "bigg boss", "web series", "daily tv", "complete season", @"s\d{2}\b", "hq predvd", @"\bhq\b", "predvd", @"\btc\b" };
            return patterns.Any(p => Regex.IsMatch(t, p));
        }

        private async Task<(List<string> Langs, bool IsAllowed)> DetectLanguageAsync(string rawTitle, string defaultLang, HashSet<string> allowedLangs, string baseName)
        {
            var foundOrig = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(baseName))
            {
                try
                {
                    var reqUrl = $"https://api.themoviedb.org/3/search/movie?api_key=f6bd687ffa63cd282b6ff2c6877f2669&query={Uri.EscapeDataString(baseName)}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, reqUrl);
                    var res = await _httpClient.SendAsync(req);
                    
                    if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        await Task.Delay(1500);
                        using var retryReq = new HttpRequestMessage(HttpMethod.Get, reqUrl);
                        res = await _httpClient.SendAsync(retryReq);
                    }

                    if (res.IsSuccessStatusCode)
                    {
                        var jsonStr = await res.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(jsonStr);
                        if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                        {
                            var firstResult = results[0];
                            if (firstResult.TryGetProperty("original_language", out var origLangEl))
                            {
                                var origLang = origLangEl.GetString()?.ToLowerInvariant();
                                var tmdbMap = new Dictionary<string, string> {
                                    { "ta", "Tamil" }, { "ml", "Malayalam" }, { "te", "Telugu" },
                                    { "kn", "Kannada" }, { "hi", "Hindi" }, { "en", "English" }
                                };
                                if (!string.IsNullOrEmpty(origLang))
                                {
                                    if (tmdbMap.TryGetValue(origLang, out var tmdbMapped))
                                        foundOrig.Add(tmdbMapped);
                                    else
                                        foundOrig.Add(origLang); // Raw code like "fr"
                                }
                            }
                        }
                    }
                }
                catch { }

                if (foundOrig.Count == 0)
                {
                    var config = Jellyfin.Plugin.JellyFetch.Plugin.Instance?.Configuration;
                    if (config != null && !string.IsNullOrWhiteSpace(config.OmdbApiKey))
                    {
                        try
                        {
                            var reqUrl = $"http://www.omdbapi.com/?apikey={config.OmdbApiKey}&t={Uri.EscapeDataString(baseName)}";
                            using var req = new HttpRequestMessage(HttpMethod.Get, reqUrl);
                            var res = await _httpClient.SendAsync(req);

                            if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests || res.StatusCode == System.Net.HttpStatusCode.Forbidden)
                            {
                                await Task.Delay(1500);
                                using var retryReq = new HttpRequestMessage(HttpMethod.Get, reqUrl);
                                res = await _httpClient.SendAsync(retryReq);
                            }

                            if (res.IsSuccessStatusCode)
                            {
                                var jsonStr = await res.Content.ReadAsStringAsync();
                                using var doc = JsonDocument.Parse(jsonStr);
                                if (doc.RootElement.TryGetProperty("Language", out var langEl))
                                {
                                    string omdbLangs = langEl.GetString() ?? "";
                                    var firstOmdbLang = omdbLangs.Split(',').Select(s => s.Trim()).FirstOrDefault();
                                    if (!string.IsNullOrEmpty(firstOmdbLang))
                                    {
                                        var validLangs = new[] { "Tamil", "Malayalam", "Telugu", "Kannada", "Hindi", "English" };
                                        var matchedLang = validLangs.FirstOrDefault(l => string.Equals(l, firstOmdbLang, StringComparison.OrdinalIgnoreCase));
                                        if (matchedLang != null)
                                            foundOrig.Add(matchedLang);
                                        else
                                            foundOrig.Add(firstOmdbLang); // Add raw, e.g. "French"
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            // STRICT ORIGINAL LANGUAGE CHECK
            if (foundOrig.Count > 0)
            {
                bool hasAllowedOrig = foundOrig.Any(ol => allowedLangs.Contains(ol));
                if (!hasAllowedOrig)
                {
                    return (new List<string>(), false); // Reject immediately if original language is excluded
                }
            }

            string t = rawTitle.ToLowerInvariant();
            var checks = new[] {
                ("telugu", "Telugu"), ("kannada", "Kannada"), ("hindi", "Hindi"),
                ("malayalam", "Malayalam"), ("tamil", "Tamil"), ("english", "English")
            };

            var foundRelease = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in checks)
            {
                if (Regex.IsMatch(t, $@"\b{c.Item1}\b")) foundRelease.Add(c.Item2);
            }

            var bracketMatch = Regex.Match(rawTitle, @"\[([^\]]+)\]");
            if (bracketMatch.Success)
            {
                var parts = bracketMatch.Groups[1].Value.Split('+').Select(p => p.Trim().ToLowerInvariant());
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    { "tam", "Tamil" }, { "mal", "Malayalam" }, { "eng", "English" },
                    { "tel", "Telugu" }, { "kan", "Kannada" }, { "hin", "Hindi" }
                };
                foreach (var p in parts)
                {
                    if (map.TryGetValue(p, out var l)) foundRelease.Add(l);
                }
            }

            if (foundRelease.Count == 0 && !string.IsNullOrEmpty(defaultLang))
            {
                foundRelease.Add(defaultLang);
            }

            // Fallback: If title doesn't specify language, assume it contains original languages
            if (foundRelease.Count == 0 && foundOrig.Count > 0)
            {
                foreach (var ol in foundOrig) foundRelease.Add(ol);
            }

            var allowedFound = foundRelease.Where(allowedLangs.Contains).ToList();
            return (allowedFound, allowedFound.Count > 0);
        }

        private (string Full, string Base, string Year) CleanMovieTitle(string rawTitle)
        {
            string clean = System.Net.WebUtility.HtmlDecode(rawTitle).Trim();
            clean = clean.Replace('\u00a0', ' ');
            clean = Regex.Replace(clean, @"(?i)(?:www\.)?1TamilMV\.[a-z]+ - ", "");
            clean = Regex.Replace(clean, @"(?i) - (?:www\.)?1TamilMV\.[a-z]+.*$", "");

            string year = "";
            string baseName = "";
            var yMatch = Regex.Match(clean, @"\((\d{4})\)");
            if (yMatch.Success)
            {
                year = yMatch.Groups[1].Value;
                baseName = clean.Substring(0, yMatch.Index).Trim();
            }
            else
            {
                var yMatch2 = Regex.Match(clean, @"\b(19\d{2}|20\d{2})\b");
                if (yMatch2.Success && yMatch2.Index > 0)
                {
                    year = yMatch2.Groups[1].Value;
                    baseName = clean.Substring(0, yMatch2.Index).Trim();
                }
                else
                {
                    baseName = Regex.Split(clean, @" - | – ")[0].Trim();
                }
            }

            baseName = Regex.Replace(baseName, @"\[.*?\]|\(.*?\)", "").Trim();
            baseName = Regex.Replace(baseName, @"[\s\-]+$", "").Trim();
            baseName = Regex.Replace(baseName, @"[\\/*?:""<>|]", "").Trim();
            baseName = baseName.TrimEnd('.', ' ');

            string full = string.IsNullOrEmpty(year) ? baseName : $"{baseName} ({year})";
            return (full, baseName, year);
        }

        public static string NormalizeKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var clean = Regex.Replace(s, @"\(\d{4}\)", "");
            clean = Regex.Replace(clean, @"\b(19\d{2}|20\d{2})\b", "");
            clean = Regex.Replace(clean.ToLowerInvariant(), @"[^a-z0-9]", "");
            if (clean.StartsWith("the") && clean.Length > 3) clean = clean.Substring(3);
            return clean;
        }

        public HashSet<string> LoadRealLibraryMovieKeys(string downDir, Action<string, double> logger = null)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Check downDir for any directories containing actual downloaded video files
            try
            {
                if (Directory.Exists(downDir))
                {
                    foreach (var dir in Directory.GetDirectories(downDir))
                    {
                        try
                        {
                            if (Directory.GetFiles(dir).Any(f => f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || 
                                                                 f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || 
                                                                 f.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)))
                            {
                                var dirName = Path.GetFileName(dir);
                                var k = NormalizeKey(dirName);
                                if (!string.IsNullOrEmpty(k)) keys.Add(k);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"Disk scan notice: {ex.Message}", -1);
            }

            // 2. Query ILibraryManager for all real movies in the library
            if (_libraryManager != null)
            {
                try
                {
                    var query = new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { BaseItemKind.Movie },
                        Recursive = true,
                        IsVirtualItem = false
                    };

                    var method = _libraryManager.GetType().GetMethod("GetItemList", new[] { typeof(InternalItemsQuery) }) 
                                 ?? typeof(ILibraryManager).GetMethod("GetItemList", new[] { typeof(InternalItemsQuery) });
                    
                    var items = (System.Collections.IEnumerable)method.Invoke(_libraryManager, new object[] { query });
                    foreach (MediaBrowser.Controller.Entities.BaseItem item in items)
                    {
                        bool isStrm = !string.IsNullOrEmpty(item.Path) && 
                                      item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

                        // If not a .strm dummy (e.g. .mkv/.mp4 on Google Drive or local storage), record it
                        if (!isStrm)
                        {
                            var k1 = NormalizeKey(item.Name);
                            if (!string.IsNullOrEmpty(k1)) keys.Add(k1);

                            if (!string.IsNullOrEmpty(item.OriginalTitle))
                            {
                                var k2 = NormalizeKey(item.OriginalTitle);
                                if (!string.IsNullOrEmpty(k2)) keys.Add(k2);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"Library pre-scan notice: {ex.Message}", -1);
                }
            }

            return keys;
        }

        private async Task<(string Title, string Poster, List<ScrapedMagnetOption> Magnets)> ScrapeTopicAsync(string url, CancellationToken ct)
        {
            url = Regex.Replace(url, @"(/page/\d+/|&do=[^&]+|#comment-\d+)", "");
            string html = "";
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                var res = await _httpClient.SendAsync(req, ct);
                html = await res.Content.ReadAsStringAsync(ct);
            }
            catch { return (null, null, new List<ScrapedMagnetOption>()); }

            var titleMatch = Regex.Match(html, @"<title>(.*?)</title>", RegexOptions.IgnoreCase);
            string title = titleMatch.Success ? titleMatch.Groups[1].Value : "";

            string poster = null;
            var posterMatch = Regex.Match(html, @"<meta\s+property=[""']og:image[""']\s+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (posterMatch.Success)
            {
                string p = posterMatch.Groups[1].Value;
                if (!p.ToLowerInvariant().Contains("default") && !p.ToLowerInvariant().Contains("logo")) poster = p;
            }

            var parsedMagnets = new List<(string uri, string dn, double xl_gb)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in Regex.Matches(html, @"magnet:\?[^\s""'<>]+"))
            {
                string raw = System.Net.WebUtility.HtmlDecode(m.Value);
                var hm = Regex.Match(raw, @"xt=urn:btih:([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
                string hash = hm.Success ? hm.Groups[1].Value.ToLowerInvariant() : raw;
                if (seen.Contains(hash)) continue;
                seen.Add(hash);

                var q = System.Web.HttpUtility.ParseQueryString(new Uri(raw).Query);
                string dn = q["dn"] ?? "";
                string cleanDn = Regex.Replace(dn, @"^(?:www\.)?1[tT]amil[mM][vV]\.[a-zA-Z0-9]+\s*[-–]\s*", "", RegexOptions.IgnoreCase);
                cleanDn = Regex.Replace(cleanDn, @"^(?:www\.)?[a-zA-Z0-9.-]+\.[a-zA-Z]{2,6}\s*[-–]\s*", "").Trim();
                
                string xl = q["xl"] ?? "0";
                double xlGb = 0;
                if (long.TryParse(xl, out long xlb)) xlGb = Math.Round(xlb / Math.Pow(1024, 3), 2);

                if (xlGb == 0)
                {
                    var sm = Regex.Match(dn, @"(\d+(?:\.\d+)?)\s*(GB|MB)", RegexOptions.IgnoreCase);
                    if (sm.Success)
                    {
                        double val = double.Parse(sm.Groups[1].Value);
                        xlGb = sm.Groups[2].Value.ToUpperInvariant() == "MB" ? Math.Round(val / 1024.0, 2) : Math.Round(val, 2);
                    }
                }

                parsedMagnets.Add((raw, string.IsNullOrEmpty(cleanDn) ? dn : cleanDn, xlGb));
            }

            // Filter and Sort: 1080p, 720p, 2160p/4k
            var filtered = parsedMagnets.Where(m => 
                m.dn.IndexOf("1080", StringComparison.OrdinalIgnoreCase) >= 0 || 
                m.dn.IndexOf("720", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.dn.IndexOf("2160", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.dn.IndexOf("4k", StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (!filtered.Any()) filtered = parsedMagnets;

            // Filter out CAM/HDCAM/PreDVD prints at magnet level.
            // If ALL remaining magnets are low quality, return empty so the whole topic is skipped.
            bool IsCamPrint(string dn)
            {
                string d = dn.ToLowerInvariant();
                return d.Contains("hdcam") || d.Contains("predvd") || d.Contains("hq predvd") ||
                       Regex.IsMatch(d, @"\bcam\b") || Regex.IsMatch(d, @"\btc\b");
            }

            var goodMagnets = filtered.Where(m => !IsCamPrint(m.dn)).ToList();
            if (goodMagnets.Count == 0)
            {
                // All magnets are theater/cam quality — skip this topic entirely
                return (null, null, new List<ScrapedMagnetOption>());
            }

            var magnets = goodMagnets.OrderByDescending(m => m.dn.IndexOf("1080", StringComparison.OrdinalIgnoreCase) >= 0)
                                  .ThenByDescending(m => m.dn.IndexOf("720", StringComparison.OrdinalIgnoreCase) >= 0)
                                  .ThenBy(m => m.xl_gb)
                                  .Select(m => new ScrapedMagnetOption { uri = m.uri, dn = m.dn, xl_gb = m.xl_gb })
                                  .ToList();

            return (title, poster, magnets);
        }

        private static string ExtractInfoHash(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return string.Empty;
            var hm = Regex.Match(uri, @"xt=urn:btih:([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
            return hm.Success ? hm.Groups[1].Value.ToUpperInvariant() : uri;
        }

        public async Task<ScrapeResult> RunScrapeAsync(string downloadsDir, Action<string, double> logger, CancellationToken ct)
        {
            string downDir = string.IsNullOrWhiteSpace(downloadsDir) ? "/media/Downloads" : downloadsDir;
            Directory.CreateDirectory(downDir);

            logger?.Invoke("Testing mirror domains...", 2);
            string domain = await GetWorkingDomainAsync(ct);
            if (domain == null) return new ScrapeResult { Error = "All 1TamilMV mirror domains are down." };
            logger?.Invoke($"Connected to {domain}", 4);

            var allowedLangs = LoadAllowedLanguages();
            logger?.Invoke($"Active languages: {string.Join(", ", allowedLangs)}", 6);
            
            // Delete previously scraped movies that belong to deselected languages
            var dirs = Directory.GetDirectories(downDir);
            foreach (var dir in dirs)
            {
                var dName = Path.GetFileName(dir);
                if (Directory.GetFiles(dir, "*.strm", SearchOption.AllDirectories).Any())
                {
                    // Check if this movie matches any of the active languages
                    var (langs, ok) = await DetectLanguageAsync(dName, "", allowedLangs, CleanMovieTitle(dName).Base);
                    if (!ok)
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
            }

            var subforumsMap = new Dictionary<string, (int Id, string Name)[]> {
                { "Tamil", new[] { (11, "Tamil WEB-HD"), (12, "Tamil HD-Rips"), (18, "Tamil Dubbed") } },
                { "Malayalam", new[] { (36, "Malayalam WEB-HD"), (37, "Malayalam HD-Rips"), (42, "Malayalam Dubbed") } },
                { "English", new[] { (49, "Hollywood WEB-HD"), (50, "Hollywood HD-Rips") } },
                { "Telugu", new[] { (24, "Telugu WEB-HD"), (25, "Telugu HD-Rips"), (31, "Telugu Dubbed") } },
                { "Hindi", new[] { (58, "Hindi WEB-HD"), (59, "Hindi HD-Rips"), (64, "Hindi Dubbed") } },
                { "Kannada", new[] { (69, "Kannada WEB-HD"), (70, "Kannada HD-Rips") } }
            };

            var tasksList = new List<(string Url, string Lang)>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Front page scan
            logger?.Invoke("Scanning front page for latest releases...", 8);
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.{domain}/");
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                var res = await _httpClient.SendAsync(req, ct);
                string html = await res.Content.ReadAsStringAsync(ct);
                var matches = Regex.Matches(html, $@"href=[""'](https://(?:www\.)?1tamilmv\.[a-z]+/index\.php\?/forums/topic/\d+-[^""'\#\s]+/)[""']");
                var fpUrls = matches.Select(m => m.Groups[1].Value).Distinct().Take(30).ToList();
                foreach (var u in fpUrls)
                {
                    if (seenUrls.Add(u)) tasksList.Add((u, ""));
                }
            }
            catch { }
            logger?.Invoke("Front page scan completed. Scanning subforums...", 10);

            // Subforums scan
            int currentLangIndex = 0;
            int totalAllowed = allowedLangs.Count;
            foreach (var lang in allowedLangs)
            {
                currentLangIndex++;
                if (!subforumsMap.ContainsKey(lang)) continue;
                double forumPct = 10.0 + ((double)currentLangIndex / (totalAllowed == 0 ? 1 : totalAllowed) * 9.0);
                logger?.Invoke($"Scanning {lang} releases...", Math.Round(forumPct, 1));
                foreach (var sf in subforumsMap[lang])
                {
                    if (ct.IsCancellationRequested) return new ScrapeResult { Error = "Cancelled" };
                    var sfUrls = new List<string>();
                    for (int p = 1; p <= 4; p++)
                    {
                        string u = p == 1 ? $"https://www.{domain}/index.php?/forums/forum/{sf.Id}-movies/" : $"https://www.{domain}/index.php?/forums/forum/{sf.Id}-movies/page/{p}/";
                        try
                        {
                            var req = new HttpRequestMessage(HttpMethod.Get, u);
                            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                            var res = await _httpClient.SendAsync(req, ct);
                            string html = await res.Content.ReadAsStringAsync(ct);
                            var matches = Regex.Matches(html, $@"href=[""'](https://(?:www\.)?1tamilmv\.[a-z]+/index\.php\?/forums/topic/\d+-[^""'\#\s]+/)[""']");
                            foreach (Match m in matches)
                            {
                                string lu = m.Groups[1].Value;
                                if (seenUrls.Add(lu)) sfUrls.Add(lu);
                            }
                        }
                        catch { continue; }
                        if (sfUrls.Count >= 100) break;
                    }
                    foreach (var u in sfUrls.Take(100)) tasksList.Add((u, lang));
                }
            }
            
            int totalTasks = tasksList.Count;
            logger?.Invoke($"Found {totalTasks} topics to scrape. Scanning library to prevent duplicates...", 20);

            var realLibraryMovies = LoadRealLibraryMovieKeys(downDir, logger);
            logger?.Invoke($"Identified {realLibraryMovies.Count} existing downloaded movies in library. Starting parallel scrape...", 22);

            int newMovies = 0;
            int processedTasks = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct };
            
            await Parallel.ForEachAsync(tasksList, options, async (task, tct) =>
            {
                try
                {
                    var (title, poster, magnets) = await ScrapeTopicAsync(task.Url, tct);
                    if (string.IsNullOrEmpty(title) || magnets.Count == 0) return;
                    if (IsExcluded(title)) return;

                    var (full, baseN, yearStr) = CleanMovieTitle(title);
                    if (string.IsNullOrEmpty(baseN) || baseN.Length < 2) return;

                    var (langs, ok) = await DetectLanguageAsync(title, task.Lang, allowedLangs, baseN);
                    if (!ok) return;

                    // STRICT ORIGINAL LANGUAGE CHECK VIA TMDB
                    // (Implemented above in DetectLanguageAsync as a fallback)

                    // Prevent duplicate entries: skip if already downloaded anywhere in the Jellyfin library
                    var normKey = NormalizeKey(baseN);
                    if (realLibraryMovies.Contains(normKey))
                    {
                        logger?.Invoke($"Skipped {full} (Already in library)", -1);
                        return;
                    }

                    string mDir = Path.Combine(downDir, full);
                    
                    // Synchronize per-movie directory to safely merge multi-language releases without race conditions
                    var sem = _movieLocks.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
                    await sem.WaitAsync(tct);
                    try
                    {
                        // If already downloaded (.mkv/.mp4/.avi), keep existing files
                        if (Directory.Exists(mDir) && Directory.GetFiles(mDir).Any(f => f.EndsWith(".mkv") || f.EndsWith(".mp4") || f.EndsWith(".avi")))
                        {
                            realLibraryMovies.Add(normKey);
                            return;
                        }

                        Directory.CreateDirectory(mDir);
                        string strm = Path.Combine(mDir, $"{full}.strm");
                        bool isBrandNew = !File.Exists(strm);
                        if (isBrandNew)
                        {
                            await File.WriteAllTextAsync(strm, "http://localhost:8096/dummy.mp4", tct);
                        }

                        if (!string.IsNullOrEmpty(poster))
                        {
                            string pPath = Path.Combine(mDir, "poster.jpg");
                            if (!File.Exists(pPath))
                            {
                                try
                                {
                                    var preq = new HttpRequestMessage(HttpMethod.Get, poster);
                                    preq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                                    var pres = await _httpClient.SendAsync(preq, tct);
                                    if (pres.IsSuccessStatusCode)
                                    {
                                        var stream = await pres.Content.ReadAsStreamAsync(tct);
                                        using var fs = new FileStream(pPath, FileMode.Create, FileAccess.Write, FileShare.None);
                                        await stream.CopyToAsync(fs, tct);
                                    }
                                }
                                catch { }
                            }
                        }

                        // Load existing options from downloads.json if present, to merge languages without overwriting
                        string jPath = Path.Combine(mDir, "downloads.json");
                        var existingOptions = new List<ScrapedMagnetOption>();
                        if (File.Exists(jPath))
                        {
                            try
                            {
                                var existingJson = await File.ReadAllTextAsync(jPath, tct);
                                var parsed = JsonSerializer.Deserialize<List<ScrapedMagnetOption>>(existingJson);
                                if (parsed != null) existingOptions.AddRange(parsed);
                            }
                            catch { }
                        }

                        // Merge new magnets into existing list
                        foreach (var m in magnets)
                        {
                            var hash = ExtractInfoHash(m.uri);
                            var existingMatch = existingOptions.FirstOrDefault(o =>
                                (!string.IsNullOrEmpty(hash) && ExtractInfoHash(o.uri) == hash) ||
                                (!string.IsNullOrEmpty(o.dn) && string.Equals(o.dn, m.dn, StringComparison.OrdinalIgnoreCase)));

                            if (existingMatch != null)
                            {
                                // Merge language tags
                                existingMatch.languages ??= new List<string>();
                                foreach (var l in langs)
                                {
                                    if (!existingMatch.languages.Contains(l, StringComparer.OrdinalIgnoreCase))
                                    {
                                        existingMatch.languages.Add(l);
                                    }
                                }
                            }
                            else
                            {
                                m.languages = new List<string>(langs);
                                existingOptions.Add(m);
                            }
                        }

                        // Sort: 1080p first, then 720p, ascending size
                        var sortedOptions = existingOptions
                            .OrderByDescending(m => m.dn.IndexOf("1080", StringComparison.OrdinalIgnoreCase) >= 0)
                            .ThenByDescending(m => m.dn.IndexOf("720", StringComparison.OrdinalIgnoreCase) >= 0)
                            .ThenBy(m => m.xl_gb)
                            .ToList();

                        await File.WriteAllTextAsync(jPath, JsonSerializer.Serialize(sortedOptions, new JsonSerializerOptions { WriteIndented = true }), tct);

                        if (isBrandNew)
                        {
                            Interlocked.Increment(ref newMovies);
                            logger?.Invoke($"Added {full} ({string.Join(", ", langs)})", -1);
                        }
                        else
                        {
                            logger?.Invoke($"Updated {full} with languages: {string.Join(", ", langs)}", -1);
                        }
                    }
                    finally
                    {
                        sem.Release();
                    }
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"Topic scan notice: {ex.Message}", -1);
                }
                finally
                {
                    int done = Interlocked.Increment(ref processedTasks);
                    if (done % 5 == 0 || done == totalTasks)
                    {
                        double pct = 20.0 + ((double)done / totalTasks * 70.0);
                        logger?.Invoke($"Scanned {done}/{totalTasks} topics...", Math.Round(pct, 1));
                    }
                }
            });

            return new ScrapeResult { Success = true, NewMoviesCount = newMovies };
        }
    }
}
