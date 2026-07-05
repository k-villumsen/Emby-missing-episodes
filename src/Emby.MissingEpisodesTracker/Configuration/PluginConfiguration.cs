using MediaBrowser.Model.Plugins;

namespace Emby.MissingEpisodesTracker.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Drop virtual episodes that have no premiere date (TMDB placeholder noise).</summary>
        public bool IgnoreNoAirDate { get; set; }

        /// <summary>Drop episodes that have not aired yet.</summary>
        public bool IgnoreUnaired { get; set; }

        /// <summary>Days after airing before an episode counts as missing (downloader grace window).</summary>
        public int GraceDays { get; set; }

        /// <summary>Exclude Season 0 / specials.</summary>
        public bool IgnoreSpecials { get; set; }

        /// <summary>Flag Ended series with a complete collection and skip them on later scans.</summary>
        public bool EnableEndedCompleteSkip { get; set; }

        /// <summary>Write an activity-log notification when a scan finds newly missing episodes.</summary>
        public bool NotifyOnNewMissing { get; set; }

        /// <summary>Emby API key used for the scan's self-call to /Shows/Missing (the only
        /// surface where Emby 4.9 exposes its dynamically computed missing episodes).</summary>
        public string ApiKey { get; set; }

        /// <summary>Base URL of this server as reachable from the server itself.</summary>
        public string ServerUrl { get; set; }

        public PluginConfiguration()
        {
            ApiKey = "";
            ServerUrl = "http://localhost:8096";
            IgnoreNoAirDate = true;
            IgnoreUnaired = true;
            GraceDays = 1;
            IgnoreSpecials = true;
            EnableEndedCompleteSkip = true;
            NotifyOnNewMissing = true;
        }
    }
}
