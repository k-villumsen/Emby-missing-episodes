# Feature Spec — Emby Missing Episodes Tracker (server plugin)

**Mode:** GREENFIELD (locked) · **Date:** 2026-07-05 · **Status:** approved by owner

## 1. Problem

Emby's built-in missing-episode machinery (library option *"Display missing episodes within
seasons"*, surfaced via Metadata Manager and the `/Shows/Missing` REST endpoint) produces a
correct list, but:

1. **It is stateless** — every query recomputes the full list from scratch. There is no memory
   of what was already reported, what is newly missing since last time, what got resolved
   (file acquired), or what the owner has chosen to ignore.
2. **It is slow on large datasets** — the full recompute cost is paid on every run, including
   for series that can never change again (ended series with a complete local collection).
3. **It has known noise sources** — metadata providers (notably TMDB) create placeholder
   episodes with **no air date** that surface as "missing"; **unaired/future** episodes
   appear; **specials (Season 0)** metadata is unreliable.

Owner's existing prototype: `Missing-Episodes.ps1` (external script → `/emby/Shows/Missing`
→ console table + CSV). Works, but stateless and re-run-from-scratch each time.
Prior art reviewed: `BillOatmanWork/EmbyMissingEpisodes` (external CLI, same endpoint,
client-side air-date lookback filter).

## 2. Goal

A proper **Emby server plugin** (C#, runs inside Emby 4.9.x) that maintains a **persistent,
incremental** missing-episodes ledger:

- A **scheduled task** scans the library using internal APIs (no HTTP round-trips).
- Results are **diffed against a persistent state file** → each missing episode is
  *new* / *known* / *resolved* / *ignored*.
- **Ended-complete optimization:** a series whose status is *Ended* and which has zero
  missing episodes (after filters) is flagged `complete` in state and **skipped on
  subsequent scans**, invalidated only by a cheap check (local episode count for that series
  changed — e.g. files deleted) or manual un-flag.
- **Correctness filters** remove the known noise (configurable).
- A **dashboard page** shows the ledger and provides ignore/un-ignore actions.
- **Notifications** (Emby activity/notification system) fire when a scan detects newly
  missing episodes.

## 3. Locked owner decisions

| Decision | Choice |
|---|---|
| Deliverable | Emby server plugin (C# .NET Standard 2.0, `MediaBrowser.Server.Core` 4.9.x) |
| Flag surface | Dashboard page **and** notifications on new detections |
| Data source | Emby's built-in virtual (missing) episodes via internal library APIs — requires the per-library *Display missing episodes* option to stay enabled (it is) |
| Filters | Drop no-air-date; drop unaired/future (with grace period); exclude Season 0 (toggleable); per-series ignore list |
| Optimization | Ended + complete series flagged and skipped in future scans |
| State model | first-seen timestamp; new-vs-known per scan; resolved history with date; per-episode ignore |
| Target server | Emby **4.9.5.0**, Linux (villumsen.net, 192.168.1.31:8096) |

## 4. Scope

**IN:** plugin skeleton (config, dashboard page, REST endpoints for the page), scan
scheduled task, state store (JSON in plugin data folder), filters, ended-complete skip,
notifications, CSV export of current ledger, install/upgrade docs.

**OUT (explicit):** independent metadata-provider lookups (TVDB/TMDB API clients);
downloader integration (Sonarr etc.); per-user views (admin-oriented tool); Jellyfin
compatibility; changing how Emby itself generates virtual episodes (provider refresh cadence
is Emby's, not ours).

## 5. Functional requirements

### FR-1 Scan task (`IScheduledTask`)
- Default trigger: daily (configurable in Emby's task scheduler as usual).
- Queries virtual episodes per series via `ILibraryManager` internal queries.
- Skips series flagged `EndedComplete` in state unless its **local (non-virtual) episode
  count** differs from the count recorded at flag time.
- Reports progress; cancellable.

### FR-2 Filters (applied at scan time; all configurable)
- `IgnoreNoAirDate` (default on): virtual episode with null premiere date → not missing.
- `IgnoreUnaired` (default on) + `GraceDays` (default 1): premiere date in the future, or
  aired less than `GraceDays` ago → not missing (gives the downloader time).
- `IgnoreSpecials` (default on): season 0 excluded.
- Series/season ignore list; per-episode ignore list.

### FR-3 State (JSON, plugin data folder — not plugin *configuration*, which stays small)
Per missing episode: series id + name, season/episode numbers, title, premiere date,
`FirstSeenUtc`, `LastSeenUtc`, `Status` (Missing | Resolved | Ignored), `ResolvedUtc?`.
Per series: `EndedComplete` flag, local episode count at flag time, `FlaggedUtc`.
Scan metadata: last scan time, duration, counts (new/known/resolved/skipped-series).
State survives plugin updates and server restarts; corrupt/missing state file → clean
rebuild on next scan (logged, not fatal).

### FR-4 Dashboard page
- Ledger table: series / S##E## / title / aired / first seen / status; grouping by series.
- Filter toggles view (New since last scan | All missing | Resolved | Ignored).
- Actions: ignore episode, ignore series, un-ignore, un-flag EndedComplete, run scan now.
- Summary header: totals + last scan time/duration.
- CSV export of current view.

### FR-5 Notifications
- On scan completion with newly missing episodes: activity-log entry / admin notification
  "N new missing episodes (M series)". Never notifies for known-but-still-missing.

### FR-6 REST endpoints (for the dashboard page; admin-authenticated)
- `GET  .../MissingEpisodes/Report?view=new|all|resolved|ignored`
- `POST .../MissingEpisodes/Ignore` (episode/series scope)
- `POST .../MissingEpisodes/Unignore`
- `POST .../MissingEpisodes/ResetEndedComplete`
- `GET  .../MissingEpisodes/Export` (CSV)

## 6. Performance targets

- Steady-state scan on a large library (thousands of series, most ended+complete):
  seconds, not minutes — dominated by the incremental subset, not library size.
- First scan (cold state) may be slow once; every later scan is incremental.
- No O(full-library) HTTP/JSON serialization — internal `InternalItemsQuery` only,
  scoped per-series or to virtual-episode item types.

## 7. Execution safety (§6 supervisor contract)

- **test_targets:** local build machine (compile only); `http://192.168.1.31:8096`
  (owner's own Emby server, **read-only** REST queries during verification, owner-provided
  API key). No other external targets.
- **data_sanitization:** no cloned production data used in tests; unit tests use synthetic
  fixtures. The existing `Missing-Episodes.ps1` embeds a live API key — flagged to owner;
  do not publish this repo without rotating/stripping it.
- **blast_radius:** nothing executes on the server until the owner manually installs the
  DLL into the Emby plugins folder and restarts. Plugin writes only to its own state file
  under the plugin data folder; library is queried read-only. Worst case = plugin disabled/
  removed by deleting the DLL.

## 8. Dependency vetting (§14)

Single external dependency: `MediaBrowser.Server.Core` (official Emby SDK, NuGet).
Provided by the runtime at execution time (`<Private>false</Private>` equivalent —
compile-time reference only), so no third-party code ships in the DLL beyond our own.
Verdict: 🟢 GREEN (official vendor SDK, pinned to the 4.9.x line matching the server).
No other runtime dependencies planned; JSON via Emby's `IJsonSerializer` (no Newtonsoft).

## 9. Risks / [VERIFY]

- **[VERIFY] exact 4.9 API signatures** (`IScheduledTask`, `IHasWebPages`, `IService`,
  virtual-episode query shape, notification API) — research in flight; compile against the
  real NuGet package is the gate.
- **[VERIFY] what exactly is slow** in the owner's current path (endpoint recompute vs.
  metadata refresh). Plugin removes the recompute+HTTP cost and adds skipping; it cannot
  speed up Emby's own provider refresh.
- Emby 4.9 is the beta channel; API drift between 4.9.x builds is possible — pin the
  package version close to 4.9.5.
- Dashboard page JS must follow Emby's AMD (`define([...])`) conventions — version-sensitive.

## 9b. As-built deviations (recorded at delivery, 2026-07-05)

- **FR-1/FR-3 ended-complete invalidation:** the "local episode count at flag time" check was
  replaced by a strictly-stronger mechanism — deleting files causes Emby to regenerate virtual
  episodes, which auto-unflags the series on the next scan. A count snapshot adds nothing the
  data source can express that this doesn't catch.
- **FR-2 season-level ignore:** deferred (series- and episode-level shipped). Add if needed.
- **FR-6 `GET .../Export`:** CSV export is client-side from the dashboard page (with formula-
  injection neutralization); no server endpoint.
- **State model addition:** `LastBecameMissingUtc` distinguishes regressions
  (resolved → missing again) so notifications and the "new" view agree.
- **Concurrency contract:** all state read-modify-write cycles go through
  `StateStore.Mutate` under one process-wide lock; the scan gathers library data outside the
  lock and applies the diff inside it, so dashboard actions can't be silently reverted.

## 10. Milestones

1. **M1 Skeleton compiles:** plugin class + config + empty scheduled task; DLL builds.
2. **M2 Scan + state:** detection, filters, diffing, ended-complete skip; unit tests on
   the pure logic (filters/diff/state) with synthetic data.
3. **M3 Surface:** dashboard page + REST endpoints + notifications + CSV export.
4. **M4 Deliver:** install docs, verification checklist on owner's server, tag v0.1.0.
