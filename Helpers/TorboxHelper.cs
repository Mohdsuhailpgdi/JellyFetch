using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyFetch.Helpers
{
    public class TorboxResult
    {
        public bool Success { get; set; }
        public string DownloadUrl { get; set; }
        public string VideoName { get; set; }
        public long SizeBytes { get; set; }
        public string Error { get; set; }
    }

    public class TorboxHelper
    {
        private readonly HttpClient _httpClient;

        public TorboxHelper(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<TorboxResult> GetDownloadUrlAsync(string apiKey, string magnetUri, CancellationToken cancellationToken)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.torbox.app/v1/api/torrents/createtorrent");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                request.Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("magnet", magnetUri)
                });

                var response = await _httpClient.SendAsync(request, cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                
                if (!response.IsSuccessStatusCode)
                {
                    return new TorboxResult { Error = $"Torbox API rejected request - {content}" };
                }

                using var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                if (!root.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
                {
                    return new TorboxResult { Error = $"Failed to add torrent - {root.GetProperty("detail").GetString()}" };
                }

                var torrentId = root.GetProperty("data").GetProperty("torrent_id").GetInt32();

                // 2. Poll until downloaded
                var pollStart = DateTime.UtcNow;
                bool cachingSuccess = false;
                JsonElement torrentData = default;

                while (DateTime.UtcNow - pollStart < TimeSpan.FromMinutes(10))
                {
                    if (cancellationToken.IsCancellationRequested) return new TorboxResult { Error = "Cancelled" };

                    try
                    {
                        var listReq = new HttpRequestMessage(HttpMethod.Get, "https://api.torbox.app/v1/api/torrents/mylist");
                        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                        listReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                        
                        var listRes = await _httpClient.SendAsync(listReq, cancellationToken);
                        if (listRes.IsSuccessStatusCode)
                        {
                            var listContent = await listRes.Content.ReadAsStringAsync(cancellationToken);
                            using var listDoc = JsonDocument.Parse(listContent);
                            var listRoot = listDoc.RootElement;

                            if (listRoot.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var t in dataArr.EnumerateArray())
                                {
                                    string idStr = t.TryGetProperty("id", out var idProp) ? idProp.ToString() : "";
                                    string hashStr = t.TryGetProperty("hash", out var hashProp) ? hashProp.GetString() : "";

                                    if (idStr == torrentId.ToString() || (hashStr != null && magnetUri.Contains(hashStr)))
                                    {
                                        string state = t.TryGetProperty("download_state", out var stateProp) ? stateProp.GetString() : "";
                                        if (state == "completed" || state == "cached")
                                        {
                                            cachingSuccess = true;
                                            torrentData = t.Clone();
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    if (cachingSuccess) break;
                    await Task.Delay(10000, cancellationToken);
                }

                if (!cachingSuccess || torrentData.ValueKind == JsonValueKind.Undefined)
                {
                    return new TorboxResult { Error = "Timeout waiting for Torbox to cache the file." };
                }

                // 3. Find largest video file and request download link
                if (!torrentData.TryGetProperty("files", out var filesArr) || filesArr.ValueKind != JsonValueKind.Array)
                {
                    return new TorboxResult { Error = "No files found in Torbox torrent." };
                }

                string[] videoExts = { ".mkv", ".mp4", ".avi", ".mov", ".flv", ".webm" };
                JsonElement largestFile = default;
                long maxSize = -1;

                foreach (var f in filesArr.EnumerateArray())
                {
                    string name = f.TryGetProperty("name", out var nameProp) ? nameProp.GetString()?.ToLower() : "";
                    if (name != null && videoExts.Any(ext => name.EndsWith(ext)))
                    {
                        long size = f.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                        if (size > maxSize)
                        {
                            maxSize = size;
                            largestFile = f.Clone();
                        }
                    }
                }

                if (maxSize == -1)
                {
                    return new TorboxResult { Error = "No valid video files found in the Torbox torrent." };
                }

                var fileId = largestFile.GetProperty("id").GetInt32();
                var fileName = largestFile.GetProperty("name").GetString();

                // Request download link
                var dlReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.torbox.app/v1/api/torrents/requestdl?token={apiKey}&torrent_id={torrentId}&file_id={fileId}");
                dlReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                dlReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                var dlRes = await _httpClient.SendAsync(dlReq, cancellationToken);
                var dlContent = await dlRes.Content.ReadAsStringAsync(cancellationToken);

                if (!dlRes.IsSuccessStatusCode)
                {
                    return new TorboxResult { Error = $"Could not get download URL - {dlContent}" };
                }

                using var dlDoc = JsonDocument.Parse(dlContent);
                var dlRoot = dlDoc.RootElement;

                if (!dlRoot.TryGetProperty("success", out var dlSuccess) || !dlSuccess.GetBoolean())
                {
                    return new TorboxResult { Error = $"Request DL failed - {dlRoot.GetProperty("detail").GetString()}" };
                }

                string downloadUrl = dlRoot.GetProperty("data").GetString();
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    return new TorboxResult { Error = "Download URL was empty." };
                }

                string vname = fileName ?? "";
                vname = Regex.Replace(vname, @"^(?:www\.)?1[tT]amil[mM][vV]\.[a-zA-Z0-9]+\s*[-–]\s*", "", RegexOptions.IgnoreCase);
                vname = Regex.Replace(vname, @"^(?:www\.)?[a-zA-Z0-9.-]+\.[a-zA-Z]{2,6}\s*[-–]\s*", "").Trim();

                long fileSizeBytes = largestFile.TryGetProperty("size", out var sProp) ? sProp.GetInt64() : 0;
                return new TorboxResult
                {
                    Success = true,
                    DownloadUrl = downloadUrl,
                    VideoName = vname,
                    SizeBytes = fileSizeBytes
                };
            }
            catch (Exception ex)
            {
                return new TorboxResult { Error = $"Unexpected error: {ex.Message}" };
            }
        }
    }
}
