# Emby Missing Episodes Tracker

A server plugin for **Emby 4.9.x** that keeps a **persistent, incremental ledger of missing
episodes** — unlike the built-in *Display missing episodes* view, which recomputes everything
from scratch on every look and remembers nothing.

## What it does

- **Scheduled scan** (daily 04:00 by default, configurable like any Emby task) queries the
  library's virtual (missing) episodes through the server's internal API — one global query,
  no HTTP round-trips, fast even on large libraries.
- **Remembers state** between scans in `tracker_state.json` (plugin data folder):
  - *New* vs. *known* missing episodes (first-seen / last-seen timestamps)
  - *Resolved* history — when a missing episode shows up in the library, it's recorded
  - *Removed* — provider metadata vanished rather than the file arriving
  - *Ignored* — per-episode and per-series ignore lists that survive rescans
- **Ended + complete skip**: a series whose status is *Ended* with nothing missing is flagged
  and skipped on later scans; it auto-unflags if real missing episodes ever reappear
  (e.g. you delete files).
- **Noise filters** (all configurable): episodes with **no air date** (TMDB placeholder junk),
  **unaired** episodes with a grace period after airing, and **specials** (Season 0).
- **Notifications**: an activity-log entry after any scan that finds *newly* missing episodes
  (never repeats for known ones).
- **Dashboard page** (Plugins → Missing Episodes Tracker): report views (new / all missing /
  resolved / ignored / series flags), ignore & un-ignore actions, run-scan button, CSV export,
  settings.
- **Admin REST API**: `GET /emby/MissingEpisodesTracker/Report?View=...`, plus POST
  `Ignore`, `Unignore`, `ResetEndedComplete` (all require an admin API key/session).

## Requirements

- Emby Server **4.9.x** (built and verified against SDK 4.9.1.90; the plugin API is
  identical in 4.8, so 4.8 servers should also work)
- The per-library option **"Display missing episodes within seasons"** must be enabled for
  your TV libraries — that is the data source.

## Build

```powershell
dotnet build src/Emby.MissingEpisodesTracker -c Release
# → src/Emby.MissingEpisodesTracker/bin/Release/netstandard2.0/Emby.MissingEpisodesTracker.dll
```

Tests:

```powershell
dotnet test tests/Emby.MissingEpisodesTracker.Tests
```

## Install (Linux server)

1. Copy `Emby.MissingEpisodesTracker.dll` to the server's plugin folder:
   `/var/lib/emby/plugins/` (deb/rpm installs) or `/config/plugins` (Docker).
2. Match ownership to the other plugins, typically: `chown emby:emby Emby.MissingEpisodesTracker.dll`
3. Restart Emby Server.
4. Dashboard → Plugins → **Missing Episodes Tracker** → run the first scan.

The single DLL is sufficient — all dependencies are provided by the server runtime.

## Repo layout

- `src/Emby.MissingEpisodesTracker/` — the plugin
  - `Core/ScanLogic.cs` — pure scan/diff engine (fully unit-tested, no SDK types)
  - `Core/Scanner.cs` — library queries (one global virtual-episode query + targeted lookups)
  - `Core/StateStore.cs` — atomic JSON state persistence
  - `ScanTask.cs`, `Plugin.cs`, `Api/`, `Web/` — Emby integration
- `tests/` — xunit tests for the scan logic
- `docs/feature_spec.md` — the locked feature spec
- `Missing-Episodes.ps1` — the original external prototype (needs `config.local.ps1` with
  `$EmbyUrl` and `$ApiKey`; kept for reference)
