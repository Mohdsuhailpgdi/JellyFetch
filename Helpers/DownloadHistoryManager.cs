using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyFetch.Helpers
{
    public class DownloadHistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string MovieName { get; set; } = string.Empty;
        public double SizeGb { get; set; }
        public string Provider { get; set; } = string.Empty; // Seedr or Torbox
        public string Status { get; set; } = string.Empty; // Completed, Failed, Stopped
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public static class DownloadHistoryManager
    {
        private static readonly string HistoryPath = System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "history.json");
        private static readonly SemaphoreSlim _lock = new(1, 1);

        public static async Task AddEntryAsync(DownloadHistoryEntry entry)
        {
            await _lock.WaitAsync();
            try
            {
                var dir = Path.GetDirectoryName(HistoryPath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                List<DownloadHistoryEntry> history = new();
                if (File.Exists(HistoryPath))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(HistoryPath);
                        var parsed = JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(json);
                        if (parsed != null) history = parsed;
                    }
                    catch { }
                }

                history.Insert(0, entry); // Add to top

                // Keep only last 100 entries
                if (history.Count > 100)
                {
                    history = history.GetRange(0, 100);
                }

                var outJson = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(HistoryPath, outJson);
            }
            finally
            {
                _lock.Release();
            }
        }

        public static async Task<List<DownloadHistoryEntry>> GetHistoryAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (File.Exists(HistoryPath))
                {
                    var json = await File.ReadAllTextAsync(HistoryPath);
                    var parsed = JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(json);
                    return parsed ?? new List<DownloadHistoryEntry>();
                }
                return new List<DownloadHistoryEntry>();
            }
            catch
            {
                return new List<DownloadHistoryEntry>();
            }
            finally
            {
                _lock.Release();
            }
        }
        
        public static async Task ClearHistoryAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (File.Exists(HistoryPath))
                {
                    File.Delete(HistoryPath);
                }
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
