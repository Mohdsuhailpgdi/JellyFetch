using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyFetch.Helpers
{
    public class SeedrResult
    {
        public bool Success { get; set; }
        public string DownloadUrl { get; set; }
        public string VideoName { get; set; }
        public string FolderId { get; set; }
        public string Error { get; set; }
    }

    public class SeedrHelper
    {
        private readonly HttpClient _httpClient;

        public SeedrHelper(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<string> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("password", password),
                new KeyValuePair<string, string>("grant_type", "password"),
                new KeyValuePair<string, string>("client_id", "seedr_chrome")
            });

            var response = await _httpClient.PostAsync("https://www.seedr.cc/oauth_test/token", content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("access_token", out var token))
                {
                    return token.GetString();
                }
            }
            return null;
        }

        private async Task<JsonElement> SeedrApiAsync(string token, string func, Dictionary<string, string> p = null, string method = "GET", CancellationToken ct = default)
        {
            string url = $"https://www.seedr.cc/oauth_test/resource.php?access_token={token}&func={func}";
            HttpResponseMessage response;
            if (p != null && method == "POST")
            {
                var content = new FormUrlEncodedContent(p);
                response = await _httpClient.PostAsync(url, content, ct);
            }
            else
            {
                if (p != null && p.Count > 0)
                {
                    url += "&" + string.Join("&", p.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
                }
                response = await _httpClient.GetAsync(url, ct);
            }

            string resContent = await response.Content.ReadAsStringAsync(ct);
            try
            {
                var doc = JsonDocument.Parse(resContent);
                return doc.RootElement.Clone();
            }
            catch
            {
                return JsonDocument.Parse("{\"error\": \"Invalid JSON\"}").RootElement.Clone();
            }
        }

        private string ExtractHash(string magnetUri)
        {
            if (string.IsNullOrEmpty(magnetUri)) return null;
            var match = Regex.Match(magnetUri, @"urn:btih:([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToLower() : null;
        }

        private long ExtractSizeBytes(string magnetUri, double fallbackGb = 2.0)
        {
            if (!string.IsNullOrEmpty(magnetUri))
            {
                var match = Regex.Match(magnetUri, @"xl=([0-9]+)");
                if (match.Success && long.TryParse(match.Groups[1].Value, out long size))
                {
                    return size;
                }
            }
            return (long)(fallbackGb * 1024 * 1024 * 1024);
        }

        private bool IsMatchingFolder(string folderName, string magnetUri, string movieName = null)
        {
            if (string.IsNullOrEmpty(folderName)) return false;
            var normFn = Regex.Replace(folderName.ToLower(), @"[^a-z0-9]", "");

            if (!string.IsNullOrEmpty(movieName))
            {
                var normMn = Regex.Replace(movieName.ToLower(), @"[^a-z0-9]", "");
                if (normFn == normMn) return true;
                if (normMn.Length > 3 && (normFn.Contains(normMn) || normMn.Contains(normFn))) return true;
            }
            
            if (string.IsNullOrEmpty(magnetUri)) return false;
            
            var match = Regex.Match(magnetUri, @"dn=([^&]+)");
            if (match.Success)
            {
                var dnClean = Uri.UnescapeDataString(match.Groups[1].Value).ToLower();
                var normDn = Regex.Replace(dnClean, @"[^a-z0-9]", "");
                
                if (normFn == normDn) return true;

                int matchLen = Math.Min(20, Math.Min(normDn.Length, normFn.Length));
                if (matchLen >= 5)
                {
                    var dnSub = normDn.Substring(0, matchLen);
                    var fnSub = normFn.Substring(0, matchLen);
                    if (normFn.Contains(dnSub) || normDn.Contains(fnSub)) return true;
                }
            }
            return false;
        }

        private async Task<JsonElement?> GetVideoFileFromFolderAsync(string token, string folderId, CancellationToken ct)
        {
            var res = await SeedrApiAsync(token, "get_folder", new Dictionary<string, string> { { "id", folderId } }, "GET", ct);
            string[] videoExts = { ".mkv", ".mp4", ".avi", ".mov", ".flv", ".webm" };
            JsonElement? bestFile = null;
            long maxSize = -1;

            if (res.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in files.EnumerateArray())
                {
                    string fname = f.TryGetProperty("name", out var nProp) ? nProp.GetString()?.ToLower() : "";
                    if (fname != null && videoExts.Any(ext => fname.EndsWith(ext)))
                    {
                        long size = f.TryGetProperty("size", out var sProp) ? sProp.GetInt64() : 0;
                        if (size > maxSize)
                        {
                            maxSize = size;
                            bestFile = f.Clone();
                        }
                    }
                }
            }
            
            if (bestFile != null) return bestFile;

            if (res.TryGetProperty("folders", out var subfolders) && subfolders.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in subfolders.EnumerateArray())
                {
                    var subId = f.GetProperty("id").ToString();
                    var subFile = await GetVideoFileFromFolderAsync(token, subId, ct);
                    if (subFile != null) return subFile;
                }
            }

            return null;
        }

        private string CleanMediaName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return "";
            var vname = Regex.Replace(rawName, @"^(?:www\.)?1[tT]amil[mM][vV]\.[a-zA-Z0-9]+\s*[-–]\s*", "", RegexOptions.IgnoreCase);
            vname = Regex.Replace(vname, @"^(?:www\.)?[a-zA-Z0-9.-]+\.[a-zA-Z]{2,6}\s*[-–]\s*", "").Trim();
            return vname;
        }

        public async Task<bool> CleanupAsync(string username, string password, string folderId, string magnetUri, CancellationToken cancellationToken)
        {
            var token = await LoginAsync(username, password, cancellationToken);
            if (token == null) return false;

            var delArr = new List<object>();
            if (!string.IsNullOrEmpty(folderId) && folderId != "null" && folderId != "None")
            {
                if (int.TryParse(folderId, out int fid))
                {
                    delArr.Add(new { type = "folder", id = fid });
                }
            }

            var infohash = ExtractHash(magnetUri);

            var root = await SeedrApiAsync(token, "get_folder", null, "GET", cancellationToken);
            if (root.TryGetProperty("torrents", out var torrents))
            {
                foreach (var t in torrents.EnumerateArray())
                {
                    string h = t.TryGetProperty("hash", out var hp) ? hp.GetString()?.ToLower() : "";
                    string n = t.TryGetProperty("name", out var np) ? np.GetString() : "";
                    if ((infohash != null && h == infohash) || (magnetUri != null && IsMatchingFolder(n, magnetUri, null)))
                    {
                        delArr.Add(new { type = "torrent", id = t.GetProperty("id").GetInt32() });
                    }
                }
            }

            var settings = await SeedrApiAsync(token, "get_settings", null, "GET", cancellationToken);
            if (settings.TryGetProperty("account", out var acc) && acc.TryGetProperty("wishlist", out var wish))
            {
                foreach (var w in wish.EnumerateArray())
                {
                    string wh = w.TryGetProperty("torrent_hash", out var whp) ? whp.GetString()?.ToLower() : "";
                    string wt = w.TryGetProperty("title", out var wtp) ? wtp.GetString() : "";
                    if ((infohash != null && wh == infohash) || (magnetUri != null && IsMatchingFolder(wt, magnetUri, null)))
                    {
                        delArr.Add(new { type = "wishlist", id = w.GetProperty("id").GetInt32() });
                    }
                }
            }

            if (delArr.Count > 0)
            {
                var delJson = JsonSerializer.Serialize(delArr);
                var res = await SeedrApiAsync(token, "delete", new Dictionary<string, string> { { "delete_arr", delJson } }, "POST", cancellationToken);
                return res.TryGetProperty("result", out var rp) && rp.ValueKind == JsonValueKind.True;
            }
            return true; // nothing to delete
        }

        public async Task<SeedrResult> GetDownloadUrlAsync(string username, string password, string magnetUri, double sizeGb, Action<int, string> onProgress, List<string> protectedFolderIds, string movieName, CancellationToken cancellationToken)
        {
            var token = await LoginAsync(username, password, cancellationToken);
            if (token == null) return new SeedrResult { Error = "Failed to login to Seedr" };

            var infohash = ExtractHash(magnetUri);
            long neededBytes = sizeGb > 0 ? (long)(sizeGb * 1024 * 1024 * 1024) : ExtractSizeBytes(magnetUri);
            double neededGb = neededBytes / Math.Pow(1024, 3);

            var root = await SeedrApiAsync(token, "get_folder", null, "GET", cancellationToken);
            var existingFolders = root.TryGetProperty("folders", out var ef) ? ef : default;
            var existingTorrents = root.TryGetProperty("torrents", out var et) ? et : default;

            var settings = await SeedrApiAsync(token, "get_settings", null, "GET", cancellationToken);
            var account = settings.TryGetProperty("account", out var ac) ? ac : default;
            long spaceUsed = account.ValueKind != JsonValueKind.Undefined && account.TryGetProperty("space_used", out var su) ? su.GetInt64() : 0;
            long spaceMax = account.ValueKind != JsonValueKind.Undefined && account.TryGetProperty("space_max", out var sm) ? sm.GetInt64() : 4294967296;
            var wishlist = account.ValueKind != JsonValueKind.Undefined && account.TryGetProperty("wishlist", out var wl) ? wl : default;

            if (existingFolders.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in existingFolders.EnumerateArray())
                {
                    string fn = f.TryGetProperty("name", out var fnProp) ? fnProp.GetString() : "";
                    if (IsMatchingFolder(fn, magnetUri, movieName))
                    {
                        var vfile = await GetVideoFileFromFolderAsync(token, f.GetProperty("id").ToString(), cancellationToken);
                        if (vfile != null)
                        {
                            string fileId = vfile.Value.TryGetProperty("folder_file_id", out var ffi) ? ffi.ToString() : vfile.Value.GetProperty("id").ToString();
                            var fetchRes = await SeedrApiAsync(token, "fetch_file", new Dictionary<string, string> { { "folder_file_id", fileId } }, "POST", cancellationToken);
                            if (fetchRes.TryGetProperty("url", out var urlProp) && !string.IsNullOrEmpty(urlProp.GetString()))
                            {
                                string vname = CleanMediaName(vfile.Value.GetProperty("name").GetString());
                                return new SeedrResult { Success = true, DownloadUrl = urlProp.GetString(), VideoName = vname, FolderId = f.GetProperty("id").ToString() };
                            }
                        }
                    }
                }
            }

            bool alreadyActive = false;
            if (existingTorrents.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in existingTorrents.EnumerateArray())
                {
                    string h = t.TryGetProperty("hash", out var hp) ? hp.GetString()?.ToLower() : "";
                    string n = t.TryGetProperty("name", out var np) ? np.GetString() : "";
                    if ((infohash != null && h == infohash) || IsMatchingFolder(n, magnetUri, movieName))
                    {
                        alreadyActive = true;
                        break;
                    }
                }
            }

            int? myWishlistId = null;
            if (wishlist.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in wishlist.EnumerateArray())
                {
                    string wh = w.TryGetProperty("torrent_hash", out var whp) ? whp.GetString()?.ToLower() : "";
                    string wt = w.TryGetProperty("title", out var wtp) ? wtp.GetString() : "";
                    if ((infohash != null && wh == infohash) || IsMatchingFolder(wt, magnetUri, movieName))
                    {
                        myWishlistId = w.GetProperty("id").GetInt32();
                        break;
                    }
                }
            }

            if (!alreadyActive && myWishlistId == null)
            {
                long spaceFree = spaceMax - spaceUsed;
                bool otherActive = existingTorrents.ValueKind == JsonValueKind.Array && existingTorrents.GetArrayLength() > 0;

                if (neededBytes > spaceMax)
                {
                    return new SeedrResult { Error = $"File ({neededGb:F1} GB) exceeds your Seedr account limit ({(spaceMax / Math.Pow(1024, 3)):F1} GB)." };
                }

                if (!otherActive && spaceFree < neededBytes && existingFolders.ValueKind == JsonValueKind.Array)
                {
                    onProgress?.Invoke(0, "Clearing old files from Seedr to make space...");
                    var delArr = new List<object>();
                    foreach (var f in existingFolders.EnumerateArray())
                    {
                        if (f.TryGetProperty("id", out var fid)) 
                        {
                            string fIdStr = fid.GetInt32().ToString();
                            if (protectedFolderIds != null && protectedFolderIds.Contains(fIdStr)) continue;
                            delArr.Add(new { type = "folder", id = fid.GetInt32() });
                        }
                    }
                    if (delArr.Count > 0)
                    {
                        var delJson = JsonSerializer.Serialize(delArr);
                        await SeedrApiAsync(token, "delete", new Dictionary<string, string> { { "delete_arr", delJson } }, "POST", cancellationToken);
                        
                        var s3 = await SeedrApiAsync(token, "get_settings", null, "GET", cancellationToken);
                        var a3 = s3.TryGetProperty("account", out var ac3) ? ac3 : default;
                        spaceUsed = a3.ValueKind != JsonValueKind.Undefined && a3.TryGetProperty("space_used", out var su3) ? su3.GetInt64() : 0;
                        spaceFree = spaceMax - spaceUsed;
                    }
                }

                bool waitingForSpace = false;
                if (otherActive)
                {
                    onProgress?.Invoke(0, "Another torrent is active in Seedr cloud. Adding to Wishlist...");
                }
                else if (spaceFree < neededBytes)
                {
                    waitingForSpace = true;
                    onProgress?.Invoke(0, $"Queued in Jellyfin (Waiting for {(neededGb):F1} GB space...)");
                }

                if (!waitingForSpace)
                {
                    var addRes = await SeedrApiAsync(token, "add_torrent", new Dictionary<string, string> { { "torrent_magnet", magnetUri } }, "POST", cancellationToken);
                    string reason = addRes.TryGetProperty("reason_phrase", out var rp) ? rp.GetString() : "";
                    string resultStr = addRes.TryGetProperty("result", out var resStr) && resStr.ValueKind == JsonValueKind.String ? resStr.GetString() : "";
                    bool resultBool = addRes.TryGetProperty("result", out var resBool) && resBool.ValueKind == JsonValueKind.True;

                    if (reason == "queue_full_added_to_wishlist" || resultStr == "wishlist" || addRes.TryGetProperty("wt", out _))
                    {
                        if (addRes.TryGetProperty("wishlist_id", out var wid)) myWishlistId = wid.GetInt32();
                        else if (addRes.TryGetProperty("wt", out var wt) && wt.TryGetProperty("id", out var wtid)) myWishlistId = wtid.GetInt32();
                        
                        onProgress?.Invoke(0, "Queued in Seedr Wishlist (Waiting for active cloud slot...)");
                    }
                    else if (resultBool || addRes.TryGetProperty("user_torrent_id", out _))
                    {
                        onProgress?.Invoke(0, "Caching started in Seedr cloud...");
                    }
                    else
                    {
                        var curSet = await SeedrApiAsync(token, "get_settings", null, "GET", cancellationToken);
                        if (curSet.TryGetProperty("account", out var cAcc) && cAcc.TryGetProperty("wishlist", out var cWish))
                        {
                            foreach (var w in cWish.EnumerateArray())
                            {
                                string wh = w.TryGetProperty("torrent_hash", out var whp) ? whp.GetString()?.ToLower() : "";
                                string wt = w.TryGetProperty("title", out var wtp) ? wtp.GetString() : "";
                                if ((infohash != null && wh == infohash) || IsMatchingFolder(wt, magnetUri, movieName))
                                {
                                    myWishlistId = w.GetProperty("id").GetInt32();
                                    break;
                                }
                            }
                        }
                        if (myWishlistId == null)
                        {
                            return new SeedrResult { Error = $"Seedr rejected torrent - {addRes.ToString()}" };
                        }
                    }
                }
            }

            var pollStart = DateTime.UtcNow;
            JsonElement? targetFolder = null;
            JsonElement? targetVideo = null;

            while (DateTime.UtcNow - pollStart < TimeSpan.FromMinutes(30))
            {
                if (cancellationToken.IsCancellationRequested) return new SeedrResult { Error = "Cancelled" };
                
                await Task.Delay(4000, cancellationToken);
                
                var r2 = await SeedrApiAsync(token, "get_folder", null, "GET", cancellationToken);
                var cFolders = r2.TryGetProperty("folders", out var cfo) ? cfo : default;
                var cTorrents = r2.TryGetProperty("torrents", out var cto) ? cto : default;

                var s2 = await SeedrApiAsync(token, "get_settings", null, "GET", cancellationToken);
                var a2 = s2.TryGetProperty("account", out var acc2) ? acc2 : default;
                long su2 = a2.ValueKind != JsonValueKind.Undefined && a2.TryGetProperty("space_used", out var ssu) ? ssu.GetInt64() : 0;
                long sm2 = a2.ValueKind != JsonValueKind.Undefined && a2.TryGetProperty("space_max", out var ssm) ? ssm.GetInt64() : 4294967296;
                long sf2 = sm2 - su2;

                if (myWishlistId != null)
                {
                    if (cTorrents.ValueKind == JsonValueKind.Array && cTorrents.GetArrayLength() == 0)
                    {
                        if (sf2 >= neededBytes)
                        {
                            onProgress?.Invoke(0, "Active cloud slot & storage available. Starting from Wishlist...");
                            var promRes = await SeedrApiAsync(token, "add_torrent", new Dictionary<string, string> { { "wishlist_id", myWishlistId.Value.ToString() } }, "POST", cancellationToken);
                            if ((promRes.TryGetProperty("result", out var pRb) && pRb.ValueKind == JsonValueKind.True) || promRes.TryGetProperty("user_torrent_id", out _))
                            {
                                myWishlistId = null;
                            }
                            else
                            {
                                string pErr = promRes.TryGetProperty("reason_phrase", out var pre) ? pre.GetString() : "Unknown Error";
                                onProgress?.Invoke(0, $"Wishlist promotion pending ({pErr})...");
                            }
                        }
                        else
                        {
                            onProgress?.Invoke(0, $"Waiting for cloud storage (need {neededGb:F1} GB, {(sf2 / Math.Pow(1024, 3)):F1} GB free)...");
                        }
                    }
                    else if (cTorrents.ValueKind == JsonValueKind.Array && cTorrents.GetArrayLength() > 0)
                    {
                        var ot = cTorrents.EnumerateArray().First();
                        string otName = ot.TryGetProperty("name", out var otn) ? otn.GetString() : "active torrent";
                        int otPct = ot.TryGetProperty("progress", out var otp) ? otp.GetInt32() : 0;
                        if (otName.Length > 20) otName = otName.Substring(0, 20);
                        onProgress?.Invoke(0, $"Queued in Wishlist (Waiting for {otName} to finish caching - {otPct}%)...");
                    }
                }

                if (cTorrents.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in cTorrents.EnumerateArray())
                    {
                        string h = t.TryGetProperty("hash", out var th) ? th.GetString()?.ToLower() : "";
                        string n = t.TryGetProperty("name", out var tn) ? tn.GetString() : "";
                        if ((infohash != null && h == infohash) || IsMatchingFolder(n, magnetUri, movieName))
                        {
                            int pct = t.TryGetProperty("progress", out var tpct) ? tpct.GetInt32() : 0;
                            double speed = t.TryGetProperty("download_speed", out var tspd) ? tspd.GetDouble() / 1024.0 / 1024.0 : 0.0;
                            int peers = t.TryGetProperty("connected_to", out var tconn) ? tconn.GetInt32() : 0;
                            onProgress?.Invoke(pct, $"Caching ({pct}% - {speed:F1} MB/s - {peers} peers)");
                            break;
                        }
                    }
                }

                if (cFolders.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in cFolders.EnumerateArray())
                    {
                        string n = f.TryGetProperty("name", out var fn) ? fn.GetString() : "";
                        if (IsMatchingFolder(n, magnetUri, movieName))
                        {
                            var vfile = await GetVideoFileFromFolderAsync(token, f.GetProperty("id").ToString(), cancellationToken);
                            if (vfile != null)
                            {
                                targetFolder = f.Clone();
                                targetVideo = vfile.Value.Clone();
                                break;
                            }
                        }
                    }
                }

                if (targetFolder != null && targetVideo != null) break;
            }

            if (targetFolder == null || targetVideo == null)
            {
                return new SeedrResult { Error = "Timeout waiting for Seedr to cache the file (0 seeders or slow peer)." };
            }

            string fFileId = targetVideo.Value.TryGetProperty("folder_file_id", out var ffi2) ? ffi2.ToString() : targetVideo.Value.GetProperty("id").ToString();
            var fetchRes2 = await SeedrApiAsync(token, "fetch_file", new Dictionary<string, string> { { "folder_file_id", fFileId } }, "POST", cancellationToken);
            if (fetchRes2.TryGetProperty("url", out var fUrl) && !string.IsNullOrEmpty(fUrl.GetString()))
            {
                string vname = CleanMediaName(targetVideo.Value.GetProperty("name").GetString());
                return new SeedrResult { Success = true, DownloadUrl = fUrl.GetString(), VideoName = vname, FolderId = targetFolder.Value.GetProperty("id").ToString() };
            }

            return new SeedrResult { Error = "Failed to obtain direct download link from Seedr." };
        }
    }
}
