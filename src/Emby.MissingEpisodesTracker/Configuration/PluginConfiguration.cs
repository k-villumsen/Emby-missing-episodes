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

        public PluginConfiguration()
        {
            IgnoreNoAirDate = true;
            IgnoreUnaired = true;
            GraceDays = 1;
            IgnoreSpecials = true;
            EnableEndedCompleteSkip = true;
            NotifyOnNewMissing = true;
        }
    }
}
