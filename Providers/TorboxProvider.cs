using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFetch.Providers;

public class TorboxProvider : IDownloaderProvider
{
    private readonly ILogger<TorboxProvider> _logger;
    private readonly HttpClient _httpClient;
    
    // Limits
    private const int MaxPerDay = 1;
    private const int MaxPerMonth = 10;
    
    // Trackers
    private int _todayDownloads = 0;
    private int _monthDownloads = 0;
    private DateTime _lastDownloadTime = DateTime.MinValue;

    public TorboxProvider(ILogger<TorboxProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("TorboxClient");
    }

    public string Name => "Torbox";

    public async Task<bool> AuthenticateAsync(string apiKeyOrUsername, string password = null)
    {
        var apiKey = apiKeyOrUsername ?? Plugin.Instance.Configuration.TorboxApiKey;
        _logger.LogInformation("Authenticating with Torbox using plugin API Key...");
        return await Task.FromResult(true);
    }

    public async Task<string> AddDownloadAsync(string magnetOrUrl, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (_lastDownloadTime.Month != now.Month)
        {
            _monthDownloads = 0;
            _todayDownloads = 0;
        }
        else if (_lastDownloadTime.Day != now.Day)
        {
            _todayDownloads = 0;
        }

        if (_monthDownloads >= MaxPerMonth)
            throw new Exception("Monthly download limit reached for Torbox.");
            
        if (_todayDownloads >= MaxPerDay)
            throw new Exception("Daily download limit reached for Torbox.");

        _logger.LogInformation("Adding download to Torbox: {Url}", magnetOrUrl);
        
        _todayDownloads++;
        _monthDownloads++;
        _lastDownloadTime = now;

        return await Task.FromResult(Guid.NewGuid().ToString());
    }
}
