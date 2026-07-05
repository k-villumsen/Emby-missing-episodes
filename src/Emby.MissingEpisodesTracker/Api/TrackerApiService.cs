using System;
using System.Collections.Generic;
using System.Linq;
using Emby.MissingEpisodesTracker.Core;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace Emby.MissingEpisodesTracker.Api
{
    [Route("/MissingEpisodesTracker/Report", "GET", Summary = "Gets the missing episodes report")]
    [Authenticated(Roles = "Admin")]
    public class GetReport : IReturn<ReportResponse>
    {
        [ApiMember(Name = "View", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string View { get; set; }
    }

    [Route("/MissingEpisodesTracker/Ignore", "POST", Summary = "Ignores an episode or a whole series")]
    [Authenticated(Roles = "Admin")]
    public class PostIgnore : IReturnVoid
    {
        public string Key { get; set; }
        public long SeriesId { get; set; }
        public string Scope { get; set; }
    }

    [Route("/MissingEpisodesTracker/Unignore", "POST", Summary = "Un-ignores an episode or a whole series")]
    [Authenticated(Roles = "Admin")]
    public class PostUnignore : IReturnVoid
    {
        public string Key { get; set; }
        public long SeriesId { get; set; }
        public string Scope { get; set; }
    }

    [Route("/MissingEpisodesTracker/ResetEndedComplete", "POST", Summary = "Clears the ended-complete flag for one or all series")]
    [Authenticated(Roles = "Admin")]
    public class PostResetEndedComplete : IReturnVoid
    {
        public long? SeriesId { get; set; }
    }

    public class ReportResponse
    {
        public ScanInfo LastScan { get; set; }
        public List<TrackedEpisode> Episodes { get; set; }
        public List<SeriesState> Series { get; set; }
        public int TotalMissing { get; set; }
    }

    public class TrackerApiService : IService
    {
        private readonly IJsonSerializer _json;

        public TrackerApiService(IJsonSerializer json)
        {
            _json = json;
        }

        public object Get(GetReport request)
        {
            var state = Plugin.Instance.CreateStateStore(_json).Load();
            var lastScanStart = state.LastScan != null ? state.LastScan.StartedUtc : DateTime.MinValue;

            IEnumerable<TrackedEpisode> episodes = state.Episodes;
            switch ((request.View ?? "missing").ToLowerInvariant())
            {
                case "new":
                    episodes = episodes.Where(e =>
                        e.Status == EpisodeStatus.Missing
                        && (e.LastBecameMissingUtc ?? e.FirstSeenUtc) >= lastScanStart);
                    break;
                case "missing":
                    episodes = episodes.Where(e => e.Status == EpisodeStatus.Missing);
                    break;
                case "resolved":
                    episodes = episodes.Where(e =>
                        e.Status == EpisodeStatus.Resolved || e.Status == EpisodeStatus.Removed);
                    break;
                case "ignored":
                    episodes = episodes.Where(e => e.Status == EpisodeStatus.Ignored);
                    break;
                default:
                    break;
            }

            return new ReportResponse
            {
                LastScan = state.LastScan,
                Episodes = episodes
                    .OrderBy(e => e.SeriesName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Season).ThenBy(e => e.Episode)
                    .ToList(),
                Series = state.Series.Where(s => s.EndedComplete || s.Ignored).ToList(),
                TotalMissing = state.Episodes.Count(e => e.Status == EpisodeStatus.Missing)
            };
        }

        public void Post(PostIgnore request)
        {
            Plugin.Instance.CreateStateStore(_json).Mutate<object>(state =>
            {
                if (string.Equals(request.Scope, "series", StringComparison.OrdinalIgnoreCase))
                {
                    var series = state.Series.FirstOrDefault(s => s.SeriesId == request.SeriesId);
                    if (series == null)
                    {
                        series = new SeriesState { SeriesId = request.SeriesId };
                        state.Series.Add(series);
                    }
                    series.Ignored = true;
                    if (series.SeriesName == null)
                    {
                        series.SeriesName = state.Episodes
                            .Where(e => e.SeriesId == request.SeriesId)
                            .Select(e => e.SeriesName).FirstOrDefault(n => n != null);
                    }
                    // Series-level ignore untracks the series; individually-ignored episodes
                    // keep their own status.
                    state.Episodes.RemoveAll(e =>
                        e.SeriesId == request.SeriesId && e.Status == EpisodeStatus.Missing);
                }
                else
                {
                    var episode = state.Episodes.FirstOrDefault(e => e.Key == request.Key);
                    if (episode != null)
                    {
                        episode.Status = EpisodeStatus.Ignored;
                    }
                }
                return null;
            });
        }

        public void Post(PostUnignore request)
        {
            Plugin.Instance.CreateStateStore(_json).Mutate<object>(state =>
            {
                if (string.Equals(request.Scope, "series", StringComparison.OrdinalIgnoreCase))
                {
                    var series = state.Series.FirstOrDefault(s => s.SeriesId == request.SeriesId);
                    if (series != null)
                    {
                        series.Ignored = false;
                    }
                    // Nothing else to do: the next scan re-tracks the series' missing episodes.
                }
                else
                {
                    var episode = state.Episodes.FirstOrDefault(e => e.Key == request.Key);
                    if (episode != null)
                    {
                        // Back to Missing; the next scan verifies and resolves/removes as needed.
                        episode.Status = EpisodeStatus.Missing;
                        episode.ResolvedUtc = null;
                        episode.LastBecameMissingUtc = DateTime.UtcNow;
                    }
                }
                return null;
            });
        }

        public void Post(PostResetEndedComplete request)
        {
            Plugin.Instance.CreateStateStore(_json).Mutate<object>(state =>
            {
                foreach (var series in state.Series.Where(s =>
                             s.EndedComplete && (request.SeriesId == null || s.SeriesId == request.SeriesId)))
                {
                    series.EndedComplete = false;
                    series.FlaggedUtc = null;
                }
                return null;
            });
        }
    }
}
