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
    /// Gathering candidates is separated from applying them so the state mutation can run
    /// atomically inside <see cref="StateStore.Mutate{T}"/>.
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

        public List<EpisodeCandidate> GatherCandidates(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (progress != null) progress.Report(5);
            var virtuals = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = true,
                Recursive = true
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (progress != null) progress.Report(30);

            var candidates = new List<EpisodeCandidate>(virtuals.Length);
            var orphans = 0;
            foreach (var episode in virtuals.OfType<Episode>())
            {
                if (episode.SeriesId <= 0)
                {
                    // Broken series linkage would collapse distinct shows into one key space.
                    orphans++;
                    continue;
                }
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
            if (orphans > 0)
            {
                _logger.Warn("Skipped {0} virtual episode(s) with no series linkage.", orphans);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (progress != null) progress.Report(45);
            return candidates;
        }

        public ScanSummary ApplyScan(TrackerState state, List<EpisodeCandidate> candidates,
            ScanOptions options, DateTime startedUtc, Stopwatch stopwatch,
            CancellationToken cancellationToken, IProgress<double> progress)
        {
            var summary = ScanLogic.Run(
                state, candidates, options, startedUtc,
                IsSeriesEnded,
                GetPhysicalEpisodeKeys,
                cancellationToken);

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
