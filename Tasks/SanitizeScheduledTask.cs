using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyFetch.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFetch.Tasks;

public class SanitizeScheduledTask : IScheduledTask
{
    private readonly ILogger<SanitizeScheduledTask> _logger;
    private readonly ILibraryManager _libraryManager;

    public SanitizeScheduledTask(ILogger<SanitizeScheduledTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public string Name => "Sanitize JellyFetch Video Metadata";

    public string Key => "SanitizeJellyFetchMetadata";

    public string Description => "Scans downloads directory and uses ffmpeg to strip 1TamilMV tags and other spam metadata from .mkv and .mp4 files.";

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
        _logger.LogInformation("Starting JellyFetch Metadata Sanitize Task...");
        progress.Report(1);
        try
        {
            await DownloadersController.ExecuteMetadataCleanupAsync(_libraryManager, _logger, progress, cancellationToken);
        }
        finally
        {
            progress.Report(100);
            _logger.LogInformation("Finished JellyFetch Metadata Sanitize Task.");
            _ = Task.Delay(10000).ContinueWith(_ =>
            {
                DownloadersController.ReportMetadataProgress("Idle", 0);
            });
        }
    }
}
