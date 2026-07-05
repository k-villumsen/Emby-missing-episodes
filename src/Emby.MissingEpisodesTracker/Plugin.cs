using System;
using System.Collections.Generic;
using Emby.MissingEpisodesTracker.Configuration;
using Emby.MissingEpisodesTracker.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.MissingEpisodesTracker
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public const string PluginName = "Missing Episodes Tracker";

        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            SetId(new Guid("1f3feded-fa2b-4497-a7a6-8fe855670455"));
        }

        public override string Name => PluginName;

        public override string Description =>
            "Persistent, incremental missing-episode tracking: remembers what was already " +
            "reported, notices resolutions, filters provider noise, and skips ended series " +
            "with complete collections.";

        public StateStore CreateStateStore(IJsonSerializer json)
        {
            var folder = DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                // A silent fallback path would fork the state across two locations; fail loud.
                throw new InvalidOperationException(
                    "Plugin data folder is not initialized yet; try again after server startup completes.");
            }
            return new StateStore(json, folder);
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "missingepisodestracker",
                    DisplayName = "Missing Episodes",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.missingepisodes.html",
                    IsMainConfigPage = true,
                    EnableInMainMenu = true,
                    MenuIcon = "video_library"
                },
                new PluginPageInfo
                {
                    Name = "missingepisodestracker.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.missingepisodes.js"
                }
            };
        }
    }
}
