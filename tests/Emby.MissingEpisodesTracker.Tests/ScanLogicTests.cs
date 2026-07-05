using System;
using System.Collections.Generic;
using System.Linq;
using Emby.MissingEpisodesTracker.Core;
using Xunit;

namespace Emby.MissingEpisodesTracker.Tests
{
    public class ScanLogicTests
    {
        private static readonly DateTime Now = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

        private static ScanOptions DefaultOptions()
        {
            return new ScanOptions
            {
                IgnoreNoAirDate = true,
                IgnoreUnaired = true,
                GraceDays = 1,
                IgnoreSpecials = true,
                EnableEndedCompleteSkip = true
            };
        }

        private static EpisodeCandidate Candidate(long seriesId, int season, int episode,
            DateTime? airedUtc, string seriesName = "Show", string title = "Ep")
        {
            return new EpisodeCandidate
            {
                SeriesId = seriesId,
                SeriesName = seriesName,
                Season = season,
                Episode = episode,
                Title = title,
                PremiereDateUtc = airedUtc
            };
        }

        private static DateTime Aired(int daysAgo)
        {
            return Now.AddDays(-daysAgo);
        }

        private static ScanSummary Run(TrackerState state, IEnumerable<EpisodeCandidate> candidates,
            ScanOptions options = null,
            Func<long, bool?> isEnded = null,
            Func<long, HashSet<string>> physical = null)
        {
            return ScanLogic.Run(
                state, candidates, options ?? DefaultOptions(), Now,
                isEnded ?? (_ => false),
                physical ?? (_ => new HashSet<string>()));
        }

        [Fact]
        public void NewMissingEpisode_IsTracked()
        {
            var state = new TrackerState();
            var summary = Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });

            var tracked = Assert.Single(state.Episodes);
            Assert.Equal(EpisodeStatus.Missing, tracked.Status);
            Assert.Equal(Now, tracked.FirstSeenUtc);
            Assert.Equal("1:S02E03", tracked.Key);
            Assert.Single(summary.NewlyMissing);
            Assert.Equal(1, summary.TotalMissing);
        }

        [Fact]
        public void SecondScan_SameEpisode_IsKnownNotNew()
        {
            var state = new TrackerState();
            Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });
            var summary = Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });

            Assert.Empty(summary.NewlyMissing);
            Assert.Equal(1, summary.KnownCount);
            Assert.Equal(Now, state.Episodes[0].FirstSeenUtc);
        }

        [Fact]
        public void NoAirDate_IsFiltered_WhenEnabled()
        {
            var state = new TrackerState();
            var summary = Run(state, new[] { Candidate(1, 1, 1, null) });

            Assert.Empty(state.Episodes);
            Assert.Empty(summary.NewlyMissing);
        }

        [Fact]
        public void NoAirDate_IsTracked_WhenFilterDisabled()
        {
            var options = DefaultOptions();
            options.IgnoreNoAirDate = false;
            var state = new TrackerState();
            var summary = Run(state, new[] { Candidate(1, 1, 1, null) }, options);

            Assert.Single(summary.NewlyMissing);
        }

        [Fact]
        public void UnairedAndGracePeriod_AreFiltered()
        {
            var state = new TrackerState();
            var summary = Run(state, new[]
            {
                Candidate(1, 1, 1, Now.AddDays(5)),          // future
                Candidate(1, 1, 2, Now.AddHours(-23)),       // inside 1-day grace
                Candidate(1, 1, 3, Now.AddHours(-25))        // past grace -> missing
            });

            var tracked = Assert.Single(state.Episodes);
            Assert.Equal("1:S01E03", tracked.Key);
            Assert.Single(summary.NewlyMissing);
        }

        [Fact]
        public void Specials_AreFiltered_WhenEnabled()
        {
            var state = new TrackerState();
            var summary = Run(state, new[] { Candidate(1, 0, 1, Aired(100)) });

            Assert.Empty(state.Episodes);
            Assert.Empty(summary.NewlyMissing);
        }

        [Fact]
        public void FilterChange_DropsStaleMissingEntry()
        {
            var options = DefaultOptions();
            options.IgnoreSpecials = false;
            var state = new TrackerState();
            Run(state, new[] { Candidate(1, 0, 1, Aired(100)) }, options);
            Assert.Single(state.Episodes);

            var summary = Run(state, new[] { Candidate(1, 0, 1, Aired(100)) }); // specials filter back on
            Assert.Empty(state.Episodes);
            Assert.Equal(1, summary.DroppedByFilter);
        }

        [Fact]
        public void IgnoredEpisode_StaysIgnoredAcrossScans()
        {
            var state = new TrackerState();
            Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });
            state.Episodes[0].Status = EpisodeStatus.Ignored;

            var summary = Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });

            Assert.Equal(EpisodeStatus.Ignored, state.Episodes[0].Status);
            Assert.Empty(summary.NewlyMissing);
            Assert.Equal(1, summary.IgnoredCount);
            Assert.Equal(0, summary.TotalMissing);
        }

        [Fact]
        public void IgnoredSeries_IsNotTrackedAtAll()
        {
            var state = new TrackerState();
            state.Series.Add(new SeriesState { SeriesId = 1, Ignored = true });

            var summary = Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });

            Assert.Empty(state.Episodes);
            Assert.Empty(summary.NewlyMissing);
            Assert.Equal(1, summary.IgnoredCount);
        }

        [Fact]
        public void IgnoredSeries_MigratesStaleMissingEntriesAway()
        {
            var state = new TrackerState();
            Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });
            Assert.Single(state.Episodes);

            state.Series.Add(new SeriesState { SeriesId = 1, Ignored = true });
            Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });

            Assert.Empty(state.Episodes);
        }

        [Fact]
        public void IgnoredSeries_DoesNotMaterializeFilteredPlaceholders()
        {
            // The M1 case: un-ignoring must not flood the ledger, so filtered placeholder
            // episodes of an ignored series must never become tracked entries.
            var state = new TrackerState();
            state.Series.Add(new SeriesState { SeriesId = 1, Ignored = true });

            Run(state, new[]
            {
                Candidate(1, 1, 1, null),           // no-air-date placeholder
                Candidate(1, 0, 1, Aired(100)),     // special
                Candidate(1, 1, 2, Aired(10))       // would be missing if not ignored
            });

            Assert.Empty(state.Episodes);
        }

        [Fact]
        public void MissingEpisode_GonePhysicallyPresent_IsResolved()
        {
            var state = new TrackerState();
            Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });

            var summary = Run(state, new EpisodeCandidate[0],
                physical: sid => new HashSet<string> { "1:S02E03" });

            Assert.Equal(EpisodeStatus.Resolved, state.Episodes[0].Status);
            Assert.Equal(Now, state.Episodes[0].ResolvedUtc);
            Assert.Equal(1, summary.ResolvedCount);
            Assert.Equal(0, summary.TotalMissing);
        }

        [Fact]
        public void MissingEpisode_GoneAndNotPhysical_IsRemoved()
        {
            var state = new TrackerState();
            Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });

            var summary = Run(state, new EpisodeCandidate[0]);

            Assert.Equal(EpisodeStatus.Removed, state.Episodes[0].Status);
            Assert.Equal(1, summary.RemovedCount);
        }

        [Fact]
        public void ResolvedEpisode_VirtualAgain_RegressesToMissingAndNotifies()
        {
            var state = new TrackerState();
            Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });
            Run(state, new EpisodeCandidate[0], physical: _ => new HashSet<string> { "1:S02E03" });
            Assert.Equal(EpisodeStatus.Resolved, state.Episodes[0].Status);

            var summary = Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });

            Assert.Equal(EpisodeStatus.Missing, state.Episodes[0].Status);
            Assert.Null(state.Episodes[0].ResolvedUtc);
            Assert.Single(summary.NewlyMissing);
            // The "new since last scan" view keys on LastBecameMissingUtc, not FirstSeenUtc.
            Assert.Equal(Now, state.Episodes[0].LastBecameMissingUtc);
        }

        [Fact]
        public void IgnoredEpisode_WhoseVirtualDisappears_IsResolvedOrRemoved()
        {
            var state = new TrackerState();
            Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });
            state.Episodes[0].Status = EpisodeStatus.Ignored;

            Run(state, new EpisodeCandidate[0], physical: _ => new HashSet<string> { "1:S02E03" });

            Assert.Equal(EpisodeStatus.Resolved, state.Episodes[0].Status);
        }

        [Fact]
        public void EndedSeries_WithOnlyFilteredVirtuals_GetsFlaggedComplete()
        {
            var state = new TrackerState();
            // Only a special is virtual; specials are filtered -> nothing missing.
            var summary = Run(state, new[] { Candidate(1, 0, 1, Aired(100)) },
                isEnded: _ => true);

            var series = Assert.Single(state.Series);
            Assert.True(series.EndedComplete);
            Assert.Contains(1L, summary.NewlyFlaggedSeries);
        }

        [Fact]
        public void ContinuingSeries_IsNotFlaggedComplete()
        {
            var state = new TrackerState();
            Run(state, new[] { Candidate(1, 0, 1, Aired(100)) }, isEnded: _ => false);

            Assert.Empty(state.Series);
        }

        [Fact]
        public void FlaggedSeries_WithRealMissing_IsAutoUnflagged()
        {
            var state = new TrackerState();
            state.Series.Add(new SeriesState { SeriesId = 1, EndedComplete = true, FlaggedUtc = Now.AddDays(-30) });

            var summary = Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });

            Assert.False(state.Series[0].EndedComplete);
            Assert.Contains(1L, summary.AutoUnflaggedSeries);
            Assert.Single(summary.NewlyMissing);
        }

        [Fact]
        public void FlaggedSeries_AbsentFromVirtuals_CountsAsSkipped()
        {
            var state = new TrackerState();
            state.Series.Add(new SeriesState { SeriesId = 99, EndedComplete = true });

            var summary = Run(state, new[] { Candidate(1, 2, 3, Aired(10)) });

            Assert.Equal(1, summary.SkippedEndedCompleteSeries);
            Assert.True(state.Series[0].EndedComplete);
        }

        [Fact]
        public void EndedSeries_JustFullyResolved_GetsFlaggedComplete()
        {
            var state = new TrackerState();
            Run(state, new[] { Candidate(1, 2, 3, Aired(10), "Ended Show") }, isEnded: _ => true);
            Assert.Empty(state.Series); // has missing -> not flagged

            var summary = Run(state, new EpisodeCandidate[0],
                isEnded: _ => true,
                physical: _ => new HashSet<string> { "1:S02E03" });

            Assert.Equal(EpisodeStatus.Resolved, state.Episodes[0].Status);
            var series = Assert.Single(state.Series);
            Assert.True(series.EndedComplete);
            Assert.Equal("Ended Show", series.SeriesName);
            Assert.Contains(1L, summary.NewlyFlaggedSeries);
        }

        [Fact]
        public void EndedCompleteSkip_Disabled_NeverFlags()
        {
            var options = DefaultOptions();
            options.EnableEndedCompleteSkip = false;
            var state = new TrackerState();

            Run(state, new[] { Candidate(1, 0, 1, Aired(100)) }, options, isEnded: _ => true);

            Assert.Empty(state.Series);
        }

        [Fact]
        public void CandidatesWithoutEpisodeNumbers_AreSkipped()
        {
            var state = new TrackerState();
            var summary = Run(state, new[]
            {
                new EpisodeCandidate { SeriesId = 1, SeriesName = "Show", Season = 1, Episode = null,
                                       PremiereDateUtc = Aired(10) }
            });

            Assert.Empty(state.Episodes);
            Assert.Equal(1, summary.SkippedNoIndex);
        }

        [Fact]
        public void SeriesEndedLookup_OnlyCalledForSeriesNeedingIt()
        {
            // A flagged ended-complete series with only filtered virtuals must not trigger lookups.
            var state = new TrackerState();
            state.Series.Add(new SeriesState { SeriesId = 1, EndedComplete = true });
            var lookups = new List<long>();

            Run(state, new[] { Candidate(1, 0, 1, Aired(100)) },
                isEnded: sid => { lookups.Add(sid); return true; });

            Assert.Empty(lookups);
            Assert.True(state.Series[0].EndedComplete); // filtered specials don't unflag
        }

        [Fact]
        public void FlaggedSeries_WithOnlyFilteredVirtuals_StillCountsAsSkipped()
        {
            var state = new TrackerState();
            state.Series.Add(new SeriesState { SeriesId = 1, EndedComplete = true });

            var summary = Run(state, new[] { Candidate(1, 0, 1, Aired(100)) }); // special -> filtered

            Assert.Equal(1, summary.SkippedEndedCompleteSeries);
        }

        [Fact]
        public void SeriesFlaggedThisScan_IsNotCountedAsSkipped()
        {
            var state = new TrackerState();
            Run(state, new[] { Candidate(1, 2, 3, Aired(10)) }, isEnded: _ => true);

            var summary = Run(state, new EpisodeCandidate[0],
                isEnded: _ => true,
                physical: _ => new HashSet<string> { "1:S02E03" });

            Assert.Contains(1L, summary.NewlyFlaggedSeries);
            Assert.Equal(0, summary.SkippedEndedCompleteSeries);
        }

        [Fact]
        public void MultiSeriesScan_ProducesCorrectTotals()
        {
            var state = new TrackerState();
            var summary = Run(state, new[]
            {
                Candidate(1, 1, 1, Aired(10), "A"),
                Candidate(1, 1, 2, Aired(9), "A"),
                Candidate(2, 3, 4, Aired(50), "B"),
                Candidate(3, 0, 1, Aired(50), "C"),      // special -> filtered
                Candidate(4, 1, 1, Now.AddDays(3), "D")  // unaired -> filtered
            });

            Assert.Equal(3, summary.NewlyMissing.Count);
            Assert.Equal(3, summary.TotalMissing);
            Assert.Equal(2, summary.NewlyMissing.Select(e => e.SeriesId).Where(id => id == 1).Count());
        }
    }
}
