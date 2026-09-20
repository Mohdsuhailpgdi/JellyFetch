using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.JellyFetch.Configuration;
using Jellyfin.Plugin.JellyFetch.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFetch.Tasks;

public class ScraperScheduledTask : IScheduledTask
{
    private readonly ILogger<ScraperScheduledTask> _logger;

    public ScraperScheduledTask(ILogger<ScraperScheduledTask> logger)
    {
        _logger = logger;
    }

    public string Name => "Scrape External Media";

    public string Key => "ExternalMediaScraper";

    public string Description => "Scrapes 1tamilmv and other configured forums for new content.";

    public string Category => "Downloads";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting External Media Scraper Task...");
        progress.Report(10);
        
        try
        {
            using var client = new HttpClient();
            var scraper = new JellyfinScraper(client);
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            
            var res = await scraper.RunScrapeAsync(config.DownloadsDirectory, (msg, pct) => 
            {
                progress.Report(pct > 0 ? pct : 0);
                _logger.LogInformation("Scraper Task: {Msg}", msg);
            }, cancellationToken);
            
            if (res.Success)
            {
                _logger.LogInformation($"Scraper finished successfully. Found {res.NewMoviesCount} new movies.");
            }
            else
            {
                _logger.LogError($"Scraper encountered an error: {res.Error}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing native C# scraper scheduled task");
        }
        
        progress.Report(100);
        _logger.LogInformation("Finished External Media Scraper Task.");
    }
}
