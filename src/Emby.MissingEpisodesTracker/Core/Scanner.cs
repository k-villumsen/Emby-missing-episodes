using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.MissingEpisodesTracker.Core
{
    /// <summary>
    /// SDK-facing scan driver. One global query for virtual episodes drives everything;
    /// per-series lookups only run for the few series that need status or resolution checks.
    /// </summary>
    public class Scanner
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public Scanner(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public ScanSummary Scan(TrackerState state, ScanOptions options,
            CancellationToken cancellationToken, IProgress<double> progress)
        {
            var stopwatch = Stopwatch.StartNew();
            var startedUtc = DateTime.UtcNow;

            if (progress != null) progress.Report(5);
            var virtuals = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = true,
                Recursive = true
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (progress != null) progress.Report(35);

            var candidates = new List<EpisodeCandidate>(virtuals.Length);
            foreach (var episode in virtuals.OfType<Episode>())
            {
                candidates.Add(new EpisodeCandidate
                {
                    SeriesId = episode.SeriesId,
                    SeriesName = episode.SeriesName,
                    Season = episode.ParentIndexNumber,
                    Episode = episode.IndexNumber,
                    Title = episode.Name,
                    PremiereDateUtc = episode.PremiereDate.HasValue
                        ? episode.PremiereDate.Value.UtcDateTime
                        : (DateTime?)null
                });
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (progress != null) progress.Report(45);

            var summary = ScanLogic.Run(
                state, candidates, options, startedUtc,
                IsSeriesEnded,
                GetPhysicalEpisodeKeys);

            state.LastScan = new ScanInfo
            {
                StartedUtc = startedUtc,
                DurationMs = stopwatch.ElapsedMilliseconds,
                NewCount = summary.NewlyMissing.Count,
                KnownCount = summary.KnownCount,
                ResolvedCount = summary.ResolvedCount,
                RemovedCount = summary.RemovedCount,
                IgnoredCount = summary.IgnoredCount,
                DroppedByFilter = summary.DroppedByFilter,
                SkippedEndedCompleteSeries = summary.SkippedEndedCompleteSeries,
                TotalMissing = summary.TotalMissing
            };

            if (progress != null) progress.Report(95);
            return summary;
        }

        private bool? IsSeriesEnded(long seriesId)
        {
            var series = _libraryManager.GetItemById(seriesId) as Series;
            if (series == null || !series.Status.HasValue)
            {
                return null;
            }
            return series.Status.Value == SeriesStatus.Ended;
        }

        private HashSet<string> GetPhysicalEpisodeKeys(long seriesId)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false,
                Recursive = true,
                AncestorIds = new[] { seriesId }
            });

            var keys = new HashSet<string>();
            foreach (var episode in items.OfType<Episode>())
            {
                if (!episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue)
                {
                    continue;
                }
                keys.Add(ScanLogic.MakeKey(seriesId, episode.ParentIndexNumber.Value, episode.IndexNumber.Value));
                if (episode.IndexNumberEnd.HasValue)
                {
                    // Multi-episode file: covers a range.
                    for (var i = episode.IndexNumber.Value + 1; i <= episode.IndexNumberEnd.Value; i++)
                    {
                        keys.Add(ScanLogic.MakeKey(seriesId, episode.ParentIndexNumber.Value, i));
                    }
                }
            }
            return keys;
        }
    }
}
