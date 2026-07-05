using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.MissingEpisodesTracker.Configuration;
using Emby.MissingEpisodesTracker.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Emby.MissingEpisodesTracker
{
    public class ScanTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IActivityManager _activityManager;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;

        public ScanTask(ILibraryManager libraryManager, IActivityManager activityManager,
            IJsonSerializer json, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _activityManager = activityManager;
            _json = json;
            _logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Name => "Scan for missing episodes";

        public string Key => "MissingEpisodesTrackerScan";

        public string Description =>
            "Incrementally scans the library for missing episodes and updates the persistent tracker state.";

        public string Category => Plugin.PluginName;

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance.Configuration;
            var store = Plugin.Instance.CreateStateStore(_json);
            var scanner = new Scanner(_libraryManager, _logger);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var startedUtc = DateTime.UtcNow;

            // Library gathering runs outside the store lock; the state read-modify-write runs
            // atomically inside it so concurrent dashboard actions can't be reverted.
            var candidates = scanner.GatherCandidates(cancellationToken, progress);
            var summary = store.Mutate(state => scanner.ApplyScan(
                state, candidates, ToOptions(config), startedUtc, stopwatch, cancellationToken, progress));

            _logger.Info(
                "Missing episodes scan done in {0} ms: {1} new, {2} known, {3} resolved, {4} removed, {5} ignored, {6} dropped by filter, {7} ended-complete series skipped, {8} missing total.",
                stopwatch.ElapsedMilliseconds, summary.NewlyMissing.Count, summary.KnownCount,
                summary.ResolvedCount, summary.RemovedCount, summary.IgnoredCount,
                summary.DroppedByFilter, summary.SkippedEndedCompleteSeries, summary.TotalMissing);

            if (config.NotifyOnNewMissing && summary.NewlyMissing.Count > 0)
            {
                var seriesCount = summary.NewlyMissing.Select(e => e.SeriesId).Distinct().Count();
                var lines = summary.NewlyMissing
                    .OrderBy(e => e.SeriesName).ThenBy(e => e.Season).ThenBy(e => e.Episode)
                    .Take(15)
                    .Select(e => string.Format("{0} S{1:D2}E{2:D2} — {3}", e.SeriesName, e.Season, e.Episode, e.Title));
                var overview = string.Join("\n", lines);
                if (summary.NewlyMissing.Count > 15)
                {
                    overview += string.Format("\n… and {0} more", summary.NewlyMissing.Count - 15);
                }

                _activityManager.Create(new ActivityLogEntry
                {
                    Name = string.Format("{0} new missing episode(s) in {1} series",
                        summary.NewlyMissing.Count, seriesCount),
                    Type = "MissingEpisodesTracker.NewMissing",
                    Severity = LogSeverity.Info,
                    Overview = overview,
                    ShortOverview = string.Format("Total missing: {0}", summary.TotalMissing),
                    Date = DateTimeOffset.UtcNow
                });
            }

            if (progress != null) progress.Report(100);
            return Task.CompletedTask;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
                }
            };
        }

        private static ScanOptions ToOptions(PluginConfiguration config)
        {
            return new ScanOptions
            {
                IgnoreNoAirDate = config.IgnoreNoAirDate,
                IgnoreUnaired = config.IgnoreUnaired,
                GraceDays = config.GraceDays,
                IgnoreSpecials = config.IgnoreSpecials,
                EnableEndedCompleteSkip = config.EnableEndedCompleteSkip
            };
        }
    }
}
