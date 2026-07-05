using System;
using System.Collections.Generic;

namespace Emby.MissingEpisodesTracker.Core
{
    public static class EpisodeStatus
    {
        public const string Missing = "Missing";
        public const string Resolved = "Resolved";
        public const string Removed = "Removed";
        public const string Ignored = "Ignored";
    }

    public class TrackedEpisode
    {
        public string Key { get; set; }
        public long SeriesId { get; set; }
        public string SeriesName { get; set; }
        public int Season { get; set; }
        public int Episode { get; set; }
        public string Title { get; set; }
        public DateTime? PremiereDateUtc { get; set; }
        public string Status { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public DateTime? ResolvedUtc { get; set; }
    }

    public class SeriesState
    {
        public long SeriesId { get; set; }
        public string SeriesName { get; set; }
        public bool EndedComplete { get; set; }
        public DateTime? FlaggedUtc { get; set; }
        public bool Ignored { get; set; }
    }

    public class ScanInfo
    {
        public DateTime StartedUtc { get; set; }
        public long DurationMs { get; set; }
        public int NewCount { get; set; }
        public int KnownCount { get; set; }
        public int ResolvedCount { get; set; }
        public int RemovedCount { get; set; }
        public int IgnoredCount { get; set; }
        public int DroppedByFilter { get; set; }
        public int SkippedEndedCompleteSeries { get; set; }
        public int TotalMissing { get; set; }
    }

    public class TrackerState
    {
        public int Version { get; set; }
        public List<TrackedEpisode> Episodes { get; set; }
        public List<SeriesState> Series { get; set; }
        public ScanInfo LastScan { get; set; }

        public TrackerState()
        {
            Version = 1;
            Episodes = new List<TrackedEpisode>();
            Series = new List<SeriesState>();
        }
    }

    /// <summary>A virtual (missing) episode as reported by the library, decoupled from SDK types.</summary>
    public class EpisodeCandidate
    {
        public long SeriesId { get; set; }
        public string SeriesName { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string Title { get; set; }
        public DateTime? PremiereDateUtc { get; set; }
    }

    public class ScanOptions
    {
        public bool IgnoreNoAirDate { get; set; }
        public bool IgnoreUnaired { get; set; }
        public int GraceDays { get; set; }
        public bool IgnoreSpecials { get; set; }
        public bool EnableEndedCompleteSkip { get; set; }
    }

    public class ScanSummary
    {
        public List<TrackedEpisode> NewlyMissing { get; set; }
        public int KnownCount { get; set; }
        public int ResolvedCount { get; set; }
        public int RemovedCount { get; set; }
        public int IgnoredCount { get; set; }
        public int DroppedByFilter { get; set; }
        public int SkippedNoIndex { get; set; }
        public List<long> AutoUnflaggedSeries { get; set; }
        public List<long> NewlyFlaggedSeries { get; set; }
        public int SkippedEndedCompleteSeries { get; set; }
        public int TotalMissing { get; set; }

        public ScanSummary()
        {
            NewlyMissing = new List<TrackedEpisode>();
            AutoUnflaggedSeries = new List<long>();
            NewlyFlaggedSeries = new List<long>();
        }
    }
}
