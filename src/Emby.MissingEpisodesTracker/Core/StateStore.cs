using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Model.Serialization;

namespace Emby.MissingEpisodesTracker.Core
{
    /// <summary>
    /// File-based JSON persistence for the tracker state. A single process-wide lock keeps the
    /// scheduled task and the API service from interleaving; all read-modify-write cycles must
    /// go through <see cref="Mutate{T}"/> so no update can be lost to a stale snapshot.
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
                return LoadUnlocked();
            }
        }

        /// <summary>Runs load → mutate → save atomically under the store lock.</summary>
        public T Mutate<T>(Func<TrackerState, T> mutator)
        {
            lock (SyncRoot)
            {
                var state = LoadUnlocked();
                var result = mutator(state);
                SaveUnlocked(state);
                return result;
            }
        }

        private TrackerState LoadUnlocked()
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
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Transient (file locked by backup/AV, permissions blip): the file is probably
                // fine — abort instead of rebuilding empty state over it.
                throw;
            }
            catch (Exception)
            {
                // Genuinely corrupt content is not fatal: keep it for post-mortem, rebuild cleanly.
                try { File.Copy(_path, _path + ".corrupt", true); } catch { }
            }
            return new TrackerState();
        }

        private void SaveUnlocked(TrackerState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            var tmp = _path + ".tmp";
            _json.SerializeToFile(state, tmp);
            if (File.Exists(_path))
            {
                File.Replace(tmp, _path, _path + ".bak");
            }
            else
            {
                File.Move(tmp, _path);
            }
        }
    }
}
