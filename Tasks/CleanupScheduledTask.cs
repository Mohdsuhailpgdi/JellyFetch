using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyFetch.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFetch.Tasks;

public class CleanupScheduledTask : IScheduledTask
{
    private readonly ILogger<CleanupScheduledTask> _logger;
    private readonly ILibraryManager _libraryManager;

    public CleanupScheduledTask(ILogger<CleanupScheduledTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public string Name => "Clean JellyFetch .strm Files";

    public string Key => "CleanJellyFetchStrm";

    public string Description => "Removes duplicate .strm files for downloaded movies, movies in deselected languages, and orphaned dummy files.";

    public string Category => "JellyFetch";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting JellyFetch .strm Cleanup Task...");
        progress.Report(5);
        await DownloadersController.ExecuteCleanupAsync(_libraryManager, _logger, progress, cancellationToken);
        progress.Report(100);
        _logger.LogInformation("Finished JellyFetch .strm Cleanup Task.");
    }
}
