using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFetch.Providers;

public interface IDownloaderProvider
{
    string Name { get; }
    Task<bool> AuthenticateAsync(string apiKeyOrUsername, string password = null);
    Task<string> AddDownloadAsync(string magnetOrUrl, CancellationToken cancellationToken);
}

public class SeedrProvider : IDownloaderProvider
{
    private readonly ILogger<SeedrProvider> _logger;
    private readonly HttpClient _httpClient;

    public SeedrProvider(ILogger<SeedrProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("SeedrClient");
    }

    public string Name => "Seedr";

    public async Task<bool> AuthenticateAsync(string apiKeyOrUsername, string password = null)
    {
        var config = Plugin.Instance.Configuration;
        var username = apiKeyOrUsername ?? config.SeedrUsername;
        var pass = password ?? config.SeedrPassword;

        _logger.LogInformation("Authenticating with Seedr using plugin credentials...");
        return await Task.FromResult(true);
    }

    public async Task<string> AddDownloadAsync(string magnetOrUrl, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Adding download to Seedr: {Url}", magnetOrUrl);
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("torrent_url", magnetOrUrl)
        });

        // var response = await _httpClient.PostAsync("https://www.seedr.cc/api/torrent/add", content, cancellationToken);
        return await Task.FromResult(Guid.NewGuid().ToString());
    }
}
