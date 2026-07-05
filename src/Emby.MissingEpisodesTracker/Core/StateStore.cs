using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Model.Serialization;

namespace Emby.MissingEpisodesTracker.Core
{
    /// <summary>
    /// File-based JSON persistence for the tracker state. A single process-wide lock keeps the
    /// scheduled task and the API service from interleaving reads/writes.
    /// </summary>
    public class StateStore
    {
        private static readonly object SyncRoot = new object();

        private readonly IJsonSerializer _json;
        private readonly string _path;

        public StateStore(IJsonSerializer json, string dataFolderPath)
        {
            _json = json;
            _path = Path.Combine(dataFolderPath, "tracker_state.json");
        }

        public TrackerState Load()
        {
            lock (SyncRoot)
            {
                try
                {
                    if (File.Exists(_path))
                    {
                        var state = _json.DeserializeFromFile<TrackerState>(_path);
                        if (state != null)
                        {
                            state.Episodes = state.Episodes ?? new List<TrackedEpisode>();
                            state.Series = state.Series ?? new List<SeriesState>();
                            return state;
                        }
                    }
                }
                catch (Exception)
                {
                    // Corrupt state is not fatal: keep it for post-mortem, rebuild cleanly.
                    try { File.Copy(_path, _path + ".corrupt", true); } catch { }
                }
                return new TrackerState();
            }
        }

        public void Save(TrackerState state)
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var tmp = _path + ".tmp";
                _json.SerializeToFile(state, tmp);
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
                File.Move(tmp, _path);
            }
        }
    }
}
