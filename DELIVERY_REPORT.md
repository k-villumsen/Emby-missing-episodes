# Delivery Report — Emby Missing Episodes Tracker v0.1.0

**Mode:** GREENFIELD · **Date:** 2026-07-05 · **Repo:** `D:\Claude.AI_work\Emby-missing-episodes` (master `532c551`)

## What was built

A C# server plugin for Emby 4.9.x (`Emby.MissingEpisodesTracker.dll`, .NET Standard 2.0,
SDK `MediaBrowser.Server.Core 4.9.1.90`) that replaces the stateless, slow built-in
missing-episodes view with a persistent, incremental tracker:

- **Scan task** (daily 04:00 default): one global internal query for virtual episodes —
  O(missing), not O(library) — with per-series lookups only where needed.
- **Persistent ledger** (`tracker_state.json` in the plugin data folder, atomic writes,
  corrupt-file quarantine): Missing / Resolved / Removed / Ignored per episode with
  first-seen / last-seen / became-missing / resolved timestamps.
- **Ended + complete skip** with automatic un-flag when real missing episodes reappear.
- **Filters**: no-air-date placeholders, unaired + grace days, specials — all configurable.
- **Dashboard page**: report views (new / missing / resolved / ignored / series flags),
  ignore & un-ignore actions, re-check, run-scan with completion polling, CSV export.
- **Activity-log notification** only for *newly* missing episodes.
- **Admin REST API**: `Report`, `Ignore`, `Unignore`, `ResetEndedComplete`.

## Gate status

| Gate | Result |
|---|---|
| Technical Lead / spec | PASS — `docs/feature_spec.md`, owner decisions locked via Q&A |
| Dependency vetting | 🟢 GREEN — single dep = official Emby SDK, pinned 4.9.1.90, compile-time only (`Private=false`, single-DLL deploy) |
| Execution safety | PASS — compile-only locally; live server touched read-only with owner's key; nothing deploys without the owner's manual install |
| Implementation | PASS — builds clean, 0 warnings; every SDK signature pre-verified by reflection against the real 4.9.1.90 assemblies |
| Quality | PASS — **26/26 unit tests** on the pure scan/diff engine |
| Review (adversarial agent) | PASS after fixes — 1 HIGH (lost-update race), 3 MED, 8 LOW found; H1/M1/M2/M3 + L1–L6/L8 fixed and re-tested; XSS/serialization/load-plumbing verified clean |
| Docs & git | PASS — README, spec with as-built deviations, 4 commits |

## Deliverable

`src/Emby.MissingEpisodesTracker/bin/Release/netstandard2.0/Emby.MissingEpisodesTracker.dll`

Install on the server (192.168.1.31): copy the DLL to `/var/lib/emby/plugins/`,
`chown emby:emby` it, restart Emby, then Dashboard → Plugins → Missing Episodes Tracker →
Run scan now. Requires "Display missing episodes within seasons" to stay enabled on the TV
libraries (currently enabled).

## Deferred / open

- **Season-level ignore** (spec FR-2) — series- and episode-level shipped; add on demand.
- **[VERIFY] live smoke test** — the plugin has not yet run on the real server; first-run
  checklist: plugin appears in dashboard, first scan completes, state file created,
  second scan is fast and reports 0 new, ignore/un-ignore round-trip works.
- The page JS follows current Emby web-client AMD conventions (verified against Trakt /
  AutoOrganize patterns); if a future Emby web client changes conventions, only `Web/` needs
  touching.
- Rotate the Emby API key in `config.local.ps1` if this repo is ever pushed anywhere —
  the key sat in `Missing-Episodes.ps1`'s git-less working tree before this project started.

## Live evidence for the premise

During this session, a read-only `GET /Shows/Missing` (the built-in recompute the plugin
replaces) against the production server did not return within several minutes on the
owner's library. The plugin's steady-state answer to the same question is a read of
`tracker_state.json`.
