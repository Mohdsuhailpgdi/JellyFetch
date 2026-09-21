using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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

    public class JellyfinScraper
    {
        private readonly HttpClient _httpClient;
        

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
                    if (list != null && list.Count > 0) return new HashSet<string>(list);
                }
                catch { }
            }
            return new HashSet<string> { "Tamil", "Malayalam", "Hindi", "Telugu", "Kannada", "English" };
        }

        private async Task<string> GetWorkingDomainAsync(CancellationToken ct)
        {
            var domains = new List<string> { "1tamilmv.meme", "1tamilmv.ing", "1tamilmv.xyz", "1tamilmv.pizza", "1tamilmv.pics", "1tamilmv.eu", "1tamilmv.tf" };
            var cacheFile = GetCacheFile();

            if (File.Exists(cacheFile))
            {
                try
                {
                    var cached = (await File.ReadAllTextAsync(cacheFile, ct)).Trim();
                    if (domains.Contains(cached))
                    {
                        domains.Remove(cached);
                        domains.Insert(0, cached);
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
            string t = title.ToLower();
            string[] patterns = { @"s\d{2}e\d{2}", @"\bseason\s?\d+\b", @"\bep\s?\d+\b", "bigg boss", "web series", "daily tv", "complete season", @"s\d{2}\b", "hq predvd", @"\bhq\b", "predvd", @"\btc\b" };
            return patterns.Any(p => Regex.IsMatch(t, p));
        }

        private (List<string> Langs, bool IsAllowed) DetectLanguage(string rawTitle, string defaultLang, HashSet<string> allowedLangs)
        {
            string t = rawTitle.ToLower();
            var checks = new[] {
                ("telugu", "Telugu"), ("kannada", "Kannada"), ("hindi", "Hindi"),
                ("malayalam", "Malayalam"), ("tamil", "Tamil"), ("english", "English")
            };

            var found = new HashSet<string>();
            foreach (var c in checks)
            {
                if (Regex.IsMatch(t, $@"\b{c.Item1}\b")) found.Add(c.Item2);
            }

            var bracketMatch = Regex.Match(rawTitle, @"\[([^\]]+)\]");
            if (bracketMatch.Success)
            {
                var parts = bracketMatch.Groups[1].Value.Split('+').Select(p => p.Trim().ToLower());
                var map = new Dictionary<string, string> { { "tam", "Tamil" }, { "mal", "Malayalam" }, { "eng", "English" }, { "tel", "Telugu" }, { "kan", "Kannada" }, { "hin", "Hindi" } };
                foreach (var p in parts)
                {
                    if (map.TryGetValue(p, out var l)) found.Add(l);
                }
            }

            if (found.Count == 0) found.Add(defaultLang);
            var allowedFound = found.Where(allowedLangs.Contains).ToList();
            return (allowedFound, allowedFound.Count > 0);
        }

        private (string Full, string Base, string Year) CleanMovieTitle(string rawTitle)
        {
            string clean = System.Net.WebUtility.HtmlDecode(rawTitle).Trim();
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
                baseName = Regex.Split(clean, @" - | – ")[0].Trim();
            }

            baseName = Regex.Replace(baseName, @"\[.*?\]|\(.*?\)", "").Trim();
            baseName = Regex.Replace(baseName, @"[\s\-]+$", "").Trim();
            baseName = Regex.Replace(baseName, @"[\\/*?:""<>|]", "").Trim();
            baseName = baseName.TrimEnd('.', ' ');

            string full = string.IsNullOrEmpty(year) ? baseName : $"{baseName} ({year})";
            return (full, baseName, year);
        }

        private async Task<(string Title, string Poster, List<object> Magnets)> ScrapeTopicAsync(string url, CancellationToken ct)
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
            catch { return (null, null, new List<object>()); }

            var titleMatch = Regex.Match(html, @"<title>(.*?)</title>", RegexOptions.IgnoreCase);
            string title = titleMatch.Success ? titleMatch.Groups[1].Value : "";

            string poster = null;
            var posterMatch = Regex.Match(html, @"<meta\s+property=[""']og:image[""']\s+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (posterMatch.Success)
            {
                string p = posterMatch.Groups[1].Value;
                if (!p.ToLower().Contains("default") && !p.ToLower().Contains("logo")) poster = p;
            }

            var parsedMagnets = new List<(string uri, string dn, double xl_gb)>();
            var seen = new HashSet<string>();

            foreach (Match m in Regex.Matches(html, @"magnet:\?[^\s""'<>]+"))
            {
                string raw = System.Net.WebUtility.HtmlDecode(m.Value);
                var hm = Regex.Match(raw, @"xt=urn:btih:([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
                string hash = hm.Success ? hm.Groups[1].Value.ToLower() : raw;
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
                        xlGb = sm.Groups[2].Value.ToUpper() == "MB" ? Math.Round(val / 1024.0, 2) : Math.Round(val, 2);
                    }
                }

                parsedMagnets.Add((raw, string.IsNullOrEmpty(cleanDn) ? dn : cleanDn, xlGb));
            }

            // Filter and Sort: 1080p first, then 720p, ascending size. Skip < 720p.
            var filtered = parsedMagnets.Where(m => 
                m.dn.IndexOf("1080", StringComparison.OrdinalIgnoreCase) >= 0 || 
                m.dn.IndexOf("720", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.dn.IndexOf("2160", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.dn.IndexOf("4k", StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (!filtered.Any()) filtered = parsedMagnets; // fallback if no explicit resolution

            var magnets = filtered.OrderByDescending(m => m.dn.IndexOf("1080", StringComparison.OrdinalIgnoreCase) >= 0)
                                  .ThenByDescending(m => m.dn.IndexOf("720", StringComparison.OrdinalIgnoreCase) >= 0)
                                  .ThenBy(m => m.xl_gb)
                                  .Select(m => new { uri = m.uri, dn = m.dn, xl_gb = m.xl_gb })
                                  .ToList<object>();

            return (title, poster, magnets);
        }

        private readonly ILibraryManager _libraryManager;

        public JellyfinScraper(HttpClient httpClient, ILibraryManager libraryManager = null)
        {
            _httpClient = httpClient;
            _libraryManager = libraryManager;
        }

        public async Task<ScrapeResult> RunScrapeAsync(string downloadsDir, Action<string, double> logger, CancellationToken ct)
        {
            string downDir = string.IsNullOrWhiteSpace(downloadsDir) ? "/media/Downloads" : downloadsDir;
            Directory.CreateDirectory(downDir);

            string domain = await GetWorkingDomainAsync(ct);
            if (domain == null) return new ScrapeResult { Error = "All 1TamilMV mirror domains are down." };

            var allowedLangs = LoadAllowedLanguages();
            logger?.Invoke($"Active languages: {string.Join(", ", allowedLangs)}", 5);

            var subforumsMap = new Dictionary<string, (int Id, string Name)[]> {
                { "Tamil", new[] { (11, "Tamil WEB-HD"), (12, "Tamil HD-Rips"), (18, "Tamil Dubbed") } },
                { "Malayalam", new[] { (36, "Malayalam WEB-HD"), (37, "Malayalam HD-Rips"), (42, "Malayalam Dubbed") } },
                { "English", new[] { (49, "Hollywood WEB-HD"), (50, "Hollywood HD-Rips") } },
                { "Telugu", new[] { (24, "Telugu WEB-HD"), (25, "Telugu HD-Rips"), (31, "Telugu Dubbed") } },
                { "Hindi", new[] { (58, "Hindi WEB-HD"), (59, "Hindi HD-Rips"), (64, "Hindi Dubbed") } },
                { "Kannada", new[] { (69, "Kannada WEB-HD"), (70, "Kannada HD-Rips") } }
            };

            var tasksList = new List<(string Url, string Lang)>();
            var seenUrls = new HashSet<string>();

            // Front page
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
                    seenUrls.Add(u);
                    tasksList.Add((u, "Tamil"));
                }
            }
            catch { }
            logger?.Invoke("Scanning front page completed. Scanning subforums...", 15);

            // Subforums
            foreach (var lang in allowedLangs)
            {
                if (!subforumsMap.ContainsKey(lang)) continue;
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
            logger?.Invoke($"Found {totalTasks} topics to scrape. Starting parallel scrape...", 20);

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

                    var (langs, ok) = DetectLanguage(title, task.Lang, allowedLangs);
                    if (!ok) return;

                    var (full, baseN, _) = CleanMovieTitle(title);
                    if (string.IsNullOrEmpty(baseN) || baseN.Length < 2) return;

                    if (_libraryManager != null)
                    {
                        var query = new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { BaseItemKind.Movie },
                            SearchTerm = baseN,
                            Limit = 10
                        };
                        var existing = _libraryManager.GetItemList(query);
                        bool alreadyExists = false;
                        foreach (var item in existing)
                        {
                            if (string.Equals(item.Name, baseN, StringComparison.OrdinalIgnoreCase) || 
                                string.Equals(item.Name, full, StringComparison.OrdinalIgnoreCase))
                            {
                                alreadyExists = true;
                                break;
                            }
                        }
                        if (alreadyExists) return;
                    }

                    string mDir = Path.Combine(downDir, full);
                    if (Directory.Exists(mDir) && Directory.GetFiles(mDir).Any(f => f.EndsWith(".mkv") || f.EndsWith(".mp4") || f.EndsWith(".avi"))) return;

                    Directory.CreateDirectory(mDir);
                    string strm = Path.Combine(mDir, $"{full}.strm");
                    if (!File.Exists(strm)) await File.WriteAllTextAsync(strm, "http://localhost:8096/dummy.mp4", tct);

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

                    string jPath = Path.Combine(mDir, "downloads.json");
                    var jList = new List<object>();
                    foreach (var m in magnets)
                    {
                        jList.Add(new
                        {
                            uri = m.GetType().GetProperty("uri").GetValue(m, null),
                            dn = m.GetType().GetProperty("dn").GetValue(m, null),
                            xl_gb = m.GetType().GetProperty("xl_gb").GetValue(m, null),
                            languages = langs
                        });
                    }
                    await File.WriteAllTextAsync(jPath, JsonSerializer.Serialize(jList, new JsonSerializerOptions { WriteIndented = true }), tct);

                    Interlocked.Increment(ref newMovies);
                    logger?.Invoke($"Added {full} ({langs.Count} langs)", -1);
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
