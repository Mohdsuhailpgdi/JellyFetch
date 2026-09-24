using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyFetch.Api;
using Jellyfin.Plugin.JellyFetch.Configuration;
using Jellyfin.Plugin.JellyFetch.Helpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFetch.Tasks;

public class ScraperScheduledTask : IScheduledTask
{
    private readonly ILogger<ScraperScheduledTask> _logger;
    private readonly ILibraryManager _libraryManager;

    public ScraperScheduledTask(ILogger<ScraperScheduledTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public string Name => "Scrape External Media";

    public string Key => "ExternalMediaScraper";

    public string Description => "Scrapes 1TamilMV and configured forums for new releases, creating .strm stubs.";

    public string Category => "JellyFetch";

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
        progress.Report(1);
        DownloadersController.ResetScrapeLogs();
        DownloadersController.ReportScrapeProgress("Starting External Media Scraper...", 1);
        
        try
        {
            using var client = new HttpClient();
            var scraper = new JellyfinScraper(client, _libraryManager);
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            
            var res = await scraper.RunScrapeAsync(config.DownloadsDirectory, (msg, pct) => 
            {
                if (pct >= 0)
                {
                    progress.Report(pct);
                }
                _logger.LogInformation("Scraper Task: {Msg}", msg);
                DownloadersController.ReportScrapeProgress(msg, pct);
            }, cancellationToken);
            
            if (res.Success)
            {
                _logger.LogInformation($"Scraper finished successfully. Found {res.NewMoviesCount} new movies.");
                if (res.NewMoviesCount > 0)
                {
                    DownloadersController.ReportScrapeProgress("Scanning Jellyfin media library...", 95);
                    progress.Report(95);
                    await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken);
                }
                DownloadersController.ReportScrapeProgress($"Completed. Found {res.NewMoviesCount} new movies.", 100);
            }
            else
            {
                _logger.LogError($"Scraper encountered an error: {res.Error}");
                DownloadersController.ReportScrapeProgress($"Failed: {res.Error}", 0);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scraper task stopped by user.");
            DownloadersController.ReportScrapeProgress("Scraper stopped by user.", 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing scraper scheduled task");
            DownloadersController.ReportScrapeProgress("Error: " + ex.Message, 0);
        }
        finally
        {
            progress.Report(100);
            _logger.LogInformation("Finished External Media Scraper Task.");
            _ = Task.Delay(10000).ContinueWith(_ =>
            {
                DownloadersController.ReportScrapeProgress("Idle", 0);
            });
        }
    }
}
