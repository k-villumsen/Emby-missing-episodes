using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Emby.MissingEpisodesTracker.Core
{
    /// <summary>
    /// Pure scan/diff logic. No SDK types; library lookups are injected as delegates so the
    /// expensive queries only run for the small set of series that actually need them.
    /// </summary>
    public static class ScanLogic
    {
        public static string MakeKey(long seriesId, int season, int episode)
        {
            return seriesId + ":S" + season.ToString("D2") + "E" + episode.ToString("D2");
        }

        /// <param name="state">Persistent tracker state; mutated in place.</param>
        /// <param name="virtualEpisodes">All virtual (missing) episodes currently in the library, unfiltered.</param>
        /// <param name="isSeriesEnded">Library lookup: is this series Status == Ended (null = unknown).</param>
        /// <param name="getPhysicalEpisodeKeys">Library lookup: keys of physically present episodes of a series.</param>
        public static ScanSummary Run(
            TrackerState state,
            IEnumerable<EpisodeCandidate> virtualEpisodes,
            ScanOptions options,
            DateTime nowUtc,
            Func<long, bool?> isSeriesEnded,
            Func<long, HashSet<string>> getPhysicalEpisodeKeys,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var summary = new ScanSummary();

            var epByKey = new Dictionary<string, TrackedEpisode>();
            foreach (var e in state.Episodes)
            {
                epByKey[e.Key] = e;
            }

            var seriesById = new Dictionary<long, SeriesState>();
            foreach (var s in state.Series)
            {
                seriesById[s.SeriesId] = s;
            }

            // Series flagged before this scan started — basis for the "skipped" statistic.
            var preFlagged = new HashSet<long>(
                state.Series.Where(s => s.EndedComplete).Select(s => s.SeriesId));

            var seenVirtual = new HashSet<string>();
            var seriesWithVirtuals = new HashSet<long>();

            foreach (var group in virtualEpisodes.GroupBy(c => c.SeriesId))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sid = group.Key;
                seriesWithVirtuals.Add(sid);

                SeriesState sstate;
                seriesById.TryGetValue(sid, out sstate);
                string seriesName = group.Select(c => c.SeriesName).FirstOrDefault(n => !string.IsNullOrEmpty(n));

                if (sstate != null && sstate.Ignored)
                {
                    // Series-level ignore is a series-level fact: candidates are not tracked at all.
                    foreach (var c in group)
                    {
                        if (!c.Season.HasValue || !c.Episode.HasValue)
                        {
                            continue;
                        }
                        var key = MakeKey(sid, c.Season.Value, c.Episode.Value);
                        seenVirtual.Add(key);
                        summary.IgnoredCount++;

                        // Migrate any stale Missing entries left from before the series was ignored.
                        TrackedEpisode stale;
                        if (epByKey.TryGetValue(key, out stale) && stale.Status == EpisodeStatus.Missing)
                        {
                            state.Episodes.Remove(stale);
                            epByKey.Remove(key);
                        }
                    }
                    if (seriesName != null && sstate.SeriesName == null)
                    {
                        sstate.SeriesName = seriesName;
                    }
                    continue;
                }

                var seriesMissingCount = 0;

                foreach (var c in group)
                {
                    if (!c.Season.HasValue || !c.Episode.HasValue)
                    {
                        summary.SkippedNoIndex++;
                        continue;
                    }

                    var key = MakeKey(sid, c.Season.Value, c.Episode.Value);
                    seenVirtual.Add(key);

                    TrackedEpisode existing;
                    epByKey.TryGetValue(key, out existing);

                    if (existing != null && existing.Status == EpisodeStatus.Ignored)
                    {
                        existing.LastSeenUtc = nowUtc;
                        summary.IgnoredCount++;
                        continue;
                    }

                    string filterReason = null;
                    if (options.IgnoreSpecials && c.Season.Value == 0)
                    {
                        filterReason = "special";
                    }
                    else if (!c.PremiereDateUtc.HasValue)
                    {
                        if (options.IgnoreNoAirDate)
                        {
                            filterReason = "no-air-date";
                        }
                    }
                    else if (options.IgnoreUnaired && c.PremiereDateUtc.Value > nowUtc.AddDays(-options.GraceDays))
                    {
                        filterReason = "unaired-or-grace";
                    }

                    if (filterReason != null)
                    {
                        // A filter now excludes it: drop any stale Missing entry so the ledger follows the config.
                        if (existing != null && existing.Status == EpisodeStatus.Missing)
                        {
                            state.Episodes.Remove(existing);
                            epByKey.Remove(key);
                            summary.DroppedByFilter++;
                        }
                        continue;
                    }

                    seriesMissingCount++;

                    if (existing == null)
                    {
                        var tracked = CreateTracked(c, sid, key, nowUtc);
                        tracked.Status = EpisodeStatus.Missing;
                        tracked.LastBecameMissingUtc = nowUtc;
                        state.Episodes.Add(tracked);
                        epByKey[key] = tracked;
                        summary.NewlyMissing.Add(tracked);
                    }
                    else
                    {
                        existing.LastSeenUtc = nowUtc;
                        existing.Title = c.Title ?? existing.Title;
                        existing.PremiereDateUtc = c.PremiereDateUtc ?? existing.PremiereDateUtc;
                        if (existing.Status != EpisodeStatus.Missing)
                        {
                            // Regression: was resolved/removed, now virtual again (file deleted).
                            existing.Status = EpisodeStatus.Missing;
                            existing.ResolvedUtc = null;
                            existing.LastBecameMissingUtc = nowUtc;
                            summary.NewlyMissing.Add(existing);
                        }
                        else
                        {
                            summary.KnownCount++;
                        }
                    }
                }

                if (sstate != null && sstate.EndedComplete && seriesMissingCount > 0)
                {
                    // Real missing episodes reappeared (e.g. files deleted): the flag no longer holds.
                    sstate.EndedComplete = false;
                    sstate.FlaggedUtc = null;
                    summary.AutoUnflaggedSeries.Add(sid);
                }
                else if (options.EnableEndedCompleteSkip && seriesMissingCount == 0
                         && (sstate == null || !sstate.EndedComplete)
                         && isSeriesEnded(sid) == true)
                {
                    if (sstate == null)
                    {
                        sstate = new SeriesState { SeriesId = sid };
                        state.Series.Add(sstate);
                        seriesById[sid] = sstate;
                    }
                    if (seriesName != null)
                    {
                        sstate.SeriesName = seriesName;
                    }
                    sstate.EndedComplete = true;
                    sstate.FlaggedUtc = nowUtc;
                    summary.NewlyFlaggedSeries.Add(sid);
                }

                if (sstate != null && seriesName != null && sstate.SeriesName == null)
                {
                    sstate.SeriesName = seriesName;
                }
            }

            // Resolution pass: previously Missing or Ignored, no longer virtual at all.
            var resolvedSeriesIds = new HashSet<long>();
            foreach (var group in state.Episodes
                         .Where(e => (e.Status == EpisodeStatus.Missing || e.Status == EpisodeStatus.Ignored)
                                     && !seenVirtual.Contains(e.Key))
                         .GroupBy(e => e.SeriesId)
                         .ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var physical = getPhysicalEpisodeKeys(group.Key) ?? new HashSet<string>();
                var hadMissing = false;
                foreach (var e in group)
                {
                    if (e.Status == EpisodeStatus.Missing)
                    {
                        hadMissing = true;
                    }
                    if (physical.Contains(e.Key))
                    {
                        e.Status = EpisodeStatus.Resolved;
                        summary.ResolvedCount++;
                    }
                    else
                    {
                        // Vanished from provider metadata rather than acquired.
                        e.Status = EpisodeStatus.Removed;
                        summary.RemovedCount++;
                    }
                    e.ResolvedUtc = nowUtc;
                }
                if (hadMissing)
                {
                    resolvedSeriesIds.Add(group.Key);
                }
            }

            // Series that just became fully resolved may now qualify as Ended+complete.
            if (options.EnableEndedCompleteSkip)
            {
                foreach (var sid in resolvedSeriesIds.Where(id => !seriesWithVirtuals.Contains(id)))
                {
                    SeriesState sstate;
                    seriesById.TryGetValue(sid, out sstate);
                    if (sstate != null && (sstate.EndedComplete || sstate.Ignored))
                    {
                        continue;
                    }
                    if (state.Episodes.Any(e => e.SeriesId == sid && e.Status == EpisodeStatus.Missing))
                    {
                        continue;
                    }
                    if (isSeriesEnded(sid) != true)
                    {
                        continue;
                    }
                    if (sstate == null)
                    {
                        sstate = new SeriesState
                        {
                            SeriesId = sid,
                            SeriesName = state.Episodes.Where(e => e.SeriesId == sid)
                                .Select(e => e.SeriesName).FirstOrDefault(n => n != null)
                        };
                        state.Series.Add(sstate);
                        seriesById[sid] = sstate;
                    }
                    sstate.EndedComplete = true;
                    sstate.FlaggedUtc = nowUtc;
                    summary.NewlyFlaggedSeries.Add(sid);
                }
            }

            // Skipped = series that entered the scan flagged and are still flagged (their deep
            // per-series checks never ran).
            summary.SkippedEndedCompleteSeries = preFlagged.Count(id =>
            {
                SeriesState s;
                return seriesById.TryGetValue(id, out s) && s.EndedComplete;
            });
            summary.TotalMissing = state.Episodes.Count(e => e.Status == EpisodeStatus.Missing);
            return summary;
        }

        private static TrackedEpisode CreateTracked(EpisodeCandidate c, long seriesId, string key, DateTime nowUtc)
        {
            return new TrackedEpisode
            {
                Key = key,
                SeriesId = seriesId,
                SeriesName = c.SeriesName,
                Season = c.Season ?? 0,
                Episode = c.Episode ?? 0,
                Title = c.Title,
                PremiereDateUtc = c.PremiereDateUtc,
                FirstSeenUtc = nowUtc,
                LastSeenUtc = nowUtc
            };
        }
    }
}
