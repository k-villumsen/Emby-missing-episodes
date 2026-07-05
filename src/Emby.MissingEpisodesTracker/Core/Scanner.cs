using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.MissingEpisodesTracker.Core
{
    /// <summary>
    /// SDK-facing scan driver. On Emby 4.9 missing episodes exist only as transient DTOs
    /// computed by the /Shows/Missing endpoint (they have no database rows — verified live:
    /// items come back with empty Ids), so gathering runs Emby's own computation once via a
    /// localhost self-call. Per-series lookups (series status, physical episodes) use fast
    /// internal queries since those ARE database rows. Gathering is separated from applying
    /// so the state mutation can run atomically inside <see cref="StateStore.Mutate{T}"/>.
    /// </summary>
    public class Scanner
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        private readonly ILibraryManager _libraryManager;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;

        public Scanner(ILibraryManager libraryManager, IJsonSerializer json, ILogger logger)
        {
            _libraryManager = libraryManager;
            _json = json;
            _logger = logger;
        }

        public async Task<List<EpisodeCandidate>> GatherCandidatesAsync(string serverUrl, string apiKey,
            CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "No API key configured. Create one under Dashboard > Advanced > API Keys and paste it into the Missing Episodes Tracker settings.");
            }
            var baseUrl = string.IsNullOrWhiteSpace(serverUrl)
                ? "http://localhost:8096"
                : serverUrl.TrimEnd('/');
            var key = Uri.EscapeDataString(apiKey.Trim());

            if (progress != null) progress.Report(2);
            var userId = await GetAnyUserIdAsync(baseUrl, key, cancellationToken).ConfigureAwait(false);
            if (progress != null) progress.Report(5);

            _logger.Info("Requesting /Shows/Missing from the server — this runs Emby's own missing-episode computation and can take minutes on a large library...");
            var url = baseUrl + "/emby/Shows/Missing?Recursive=true&Fields=PremiereDate&UserId=" + userId + "&api_key=" + key;

            MissingItemsResponse data;
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    data = _json.DeserializeFromStream<MissingItemsResponse>(stream);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();

            var items = data != null && data.Items != null ? data.Items : new MissingItemDto[0];
            var candidates = new List<EpisodeCandidate>(items.Length);
            var orphans = 0;
            foreach (var item in items)
            {
                long seriesId = 0;
                if (!string.IsNullOrEmpty(item.SeriesId))
                {
                    try { seriesId = _libraryManager.GetInternalId(item.SeriesId); }
                    catch { seriesId = 0; }
                }
                if (seriesId <= 0)
                {
                    orphans++;
                    continue;
                }
                candidates.Add(new EpisodeCandidate
                {
                    SeriesId = seriesId,
                    SeriesName = item.SeriesName,
                    Season = item.ParentIndexNumber,
                    Episode = item.IndexNumber,
                    Title = item.Name,
                    PremiereDateUtc = item.PremiereDate.HasValue
                        ? item.PremiereDate.Value.UtcDateTime
                        : (DateTime?)null
                });
            }
            if (orphans > 0)
            {
                _logger.Warn("Skipped {0} missing episode(s) with no resolvable series id.", orphans);
            }
            _logger.Info("/Shows/Missing returned {0} item(s); {1} usable candidates.", items.Length, candidates.Count);
            if (progress != null) progress.Report(45);
            return candidates;
        }

        private async Task<string> GetAnyUserIdAsync(string baseUrl, string escapedApiKey, CancellationToken cancellationToken)
        {
            using (var response = await Http.GetAsync(baseUrl + "/emby/Users?api_key=" + escapedApiKey, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var users = _json.DeserializeFromStream<UserIdDto[]>(stream);
                    if (users == null || users.Length == 0 || string.IsNullOrEmpty(users[0].Id))
                    {
                        throw new InvalidOperationException("Could not resolve a user id via /Users (required by /Shows/Missing).");
                    }
                    return users[0].Id;
                }
            }
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

        // DTOs for the /Shows/Missing and /Users self-calls.
        public class MissingItemsResponse
        {
            public MissingItemDto[] Items { get; set; }
            public int TotalRecordCount { get; set; }
        }

        public class MissingItemDto
        {
            public string Name { get; set; }
            public string SeriesName { get; set; }
            public string SeriesId { get; set; }
            public int? ParentIndexNumber { get; set; }
            public int? IndexNumber { get; set; }
            public DateTimeOffset? PremiereDate { get; set; }
        }

        public class UserIdDto
        {
            public string Id { get; set; }
        }
    }
}
