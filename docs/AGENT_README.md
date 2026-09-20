# Agent README — VRCX Insights v4 legacy-cache

Read this together with root `AGENTS.md`, `AI_HANDOFF.md`, and the human README before changing code. This is an implementation guide, not an instruction to invent evidence or to ignore the user's instructions.

## Repository and delivery boundaries

Write target: **RICHARDwuxiaofei/VRCX_stalk**. Working branch: **feature/legacy-db-adapter-v4-20260920**. Base release commit: `31a8ede8c1d4225d21d292b70ea646535c4c2bf5`.

Do not push to `master`, rewrite history, delete branches, create or reopen upstream pull requests, or publish to `vrcx-team/VRCX` / `FuLuTang/VRCX-jirai`. The user explicitly forbids accidental upstream PRs. No PR is needed to build this branch. Never rely on GitHub CLI's implicit fork/upstream selection: use `--repo RICHARDwuxiaofei/VRCX_stalk` for releases and check `GITHUB_REPOSITORY` first.

Before a write or release, re-read the current ref and preserve other contributors' changes. The branch's integration base remains upstream v2026.09.16; do not silently upgrade it. The user's acceptance condition is completed successful Actions plus a downloadable tested Windows preview, not merely a queued run or a pushed source commit.

## What v4 fixes

The active v2 route loaded whole historic tables into JS, rejected more than 5,000 Locations / 50,000 log rows, and only sliced the UI. Raising limits is not a solution. v4 keeps the separate native SQLite cache and adds a read-only historical-schema adapter, bounded source batches, persisted checkpoints, derived presence sessions and backend pagination.

Opening the page must read only cache metadata. `create` creates the file/schema without scanning the source. `start`/`step` are invoked only after the user presses Start/Resume/Update. Opening a ready cache is a separate action. Do not add an onMounted all-history scan, automatic cache-update timer, or background analysis on app startup.

## Main files

| Path | Responsibility |
| --- | --- |
| `Dotnet/Insights/InsightsCache.cs` | Identity, schema, source adapters, per-table cursors, import transactions, native action dispatch |
| `Dotnet/Insights/InsightsCache.Legacy.cs` | Detects historical official table/column variants and projects them into canonical fields without modifying the source |
| `Dotnet/Insights/InsightsCache.Analysis.cs` | Session materialization, cached people/events/summary SQL |
| `Dotnet/Insights/SQLiteInsightsBridge.cs` | Existing native SQLite object → guarded JSON cache request |
| `src/features/local-insights/cacheClient.js` | Account-checked native client; explicit analysis loop; range conversion; v2 group preference compatibility |
| `CacheReview.vue` in that directory | Opt-in file UI, lifecycle cancellation, selectors, groups and paged reviews |
| `CachePager.vue` | 10-row preview, +20 expansion, page-size selection and moving five-page window |
| `build-scripts/apply-local-insights.mjs` | Exact idempotent route/native class/nav registry/app-isolation integration |
| `tests/InsightsCache.Native` | Actual SQLite large-history and persistence integration harness |
| `tests/InsightsCache.Edges` | Actual SQLite evidence/duplicate/late-boundary/group edge harness |
| `tests/local-insights/cache-ui.test.js` | Mounted Vue tests with mocked native transport, using the real client |
| `tests/local-insights/cache-integration.test.mjs` | Asserts applied registry/nav/route/native-bridge integration |

`ActivityReview.vue`, `reader.js`, `readDatabase.mjs` and the old pure JS analyzer remain as legacy regression material. They must not be the active v3 route, a fallback for missing native cache code, or a workaround for large-history errors.

## Data ownership and filesystem

Use the actual active `SQLite.m_Connection.DataSource`, not a guessed official profile path. The source connection opened by this subsystem is **read-only**. The normal VRCX app may continue logging through its own existing connection; that is separate from analytics.

Cache path:

```text
<Program.AppDataDirectory>/AnalyticsCache/<account ID>/<normalized-source-path SHA256 prefix>/analysis-v1.db
```

The hash fingerprints the source path, NOT the entire source file. Metadata also stores the account and source path. Separate accounts and source paths get different derived files. Do not claim historical global GameLog rows have perfect account attribution: the upstream schema does not supply it.

Cache state machine: `missing → created → ingesting → deriving → ready`. A paused build retains ingesting/deriving plus committed cursor and job ID. `rebuild` preserves the old generated database as a `.previous-...` file and returns to created. A new job ID invalidates stale step requests. Schema mismatch is reported as incompatible; no source schema migration is performed.

Named mutex serialization is scoped to the cache path. Connections do not pool. Writes use short per-batch transactions; raw result readers are disposed before cache mutation. Do not wrap the whole history in one transaction or return entire tables over the CEF boundary.

## Legacy official database compatibility

Historical official VRCX databases are normalized during import, never migrated in place. `ResolveSourceMaps` only selects whitelisted global/account tables and canonicalizes known aliases such as snake_case/camelCase timestamp, user, display-name and world fields. It may use SQLite `rowid` when a historical event table lacks an explicit numeric `id`. Old `DisplayName (usr_...)` rows may recover the embedded ID. Known old join/leave type aliases and Unix second/millisecond timestamps are normalized.

Do not broaden this into heuristic table scraping across unrelated accounts. Account-prefixed tables must still match the active account's known prefix forms. Do not infer missing instance locations. If the required Location or join/leave evidence cannot be interpreted safely, fail with an explicit compatibility error and leave the source untouched.

The cache records `source_profile=current|legacy-adapted`, adapter count, warnings and logical→physical table mapping. A changed adapter signature requires rebuilding derived cache data. Add real SQLite legacy fixtures for every new alias.

## Source adapters and incremental behavior

The whitelist includes global Location/join-leave and account-prefixed status, Bio, GPS, online/offline, avatar, friend-history, and current-friend tables. Current friends are a replaceable snapshot, not an append-only event source: removing a friend must not force reindexing all encounter history.

`PRAGMA table_info` inspects known schemas. Optional absent tables are recorded as coverage warnings. Required id/created_at columns for event tables are validated. Only known detail fields are copied; unknown fields do not silently become new analytics semantics.

Each event source snapshots MAX(id) and row count. Batches use `id > cursor AND id <= high ORDER BY id LIMIT 2000`. Normal incremental updates replay the final 1,000 ID range and upsert by `(source, source_id)`; unchanged fingerprints are skipped. Counts/IDs going backwards or table schema changes reject reuse and ask for rebuilding. Friend snapshots are refreshed separately.

**Important limitation:** a source replacement retaining the same schema, count and maximum IDs, or an edit outside the replay tail, is not guaranteed to be detected. Human UI/docs instruct rebuilding after replacement, bulk imports, or older-row edits. Do not describe this as universal change-data-capture. New rows with old timestamps but new IDs ARE read and invalidate the affected derivation range.

If any encounter event changes, replay materialization from the preceding observer Location boundary. Drop/rebuild only affected derived sessions from that point onward. A late historical boundary may require a long replay; bounded memory does not mean constant execution time.

## Cache schema and evidence rules

`events` holds normalized epoch-ms times and provenance; `people` powers autocomplete; `sessions` holds complete/incomplete pairs; `open_sessions` retains currently unmatched joins; `cursors`, `meta`, `rejected` hold resumable state and diagnostics.

Times are UTC epoch milliseconds. Offset-bearing timestamps are normalized; known legacy VRCX unzoned SQLite timestamps are interpreted as UTC. Malformed timestamps are diagnosed, not treated as local time or guessed. Query intervals are `[since, until)`.

Ordering uses `(at_ms, ord, user_id, location, seq)`, with leave before Location before join at equal timestamps. Derivation is keyset-paged, with the last duplicate signature persisted across batch boundaries. Identical evidence must not double-count durations.

Count duration only for a complete positive join/leave pair of at most 24 hours, in an actually recorded observer visit. Location boundaries close uncertain open records as incomplete, not as invented exits. Private/offline/traveling labels do not establish a visible instance. Repeated joins invalidate the uncertain earlier start. Never extend an unclosed session to now.

Pairwise summaries require the same observer `visit_id`, exact instance `location`, and overlapping complete intervals. Person summaries return that person's observed peers; group summaries restrict BOTH endpoints to members and deduplicate pair order. Send `groupOnly=true` for a group overview even with just one member; otherwise it is incorrectly interpreted as a single-person companion query.

Excluded actor: `usr_1cf3480c-c735-447d-9227-8e1acbb6bf18`, plus current account. This inherited, documented exclusion applies to this analytics subsystem, not all original logging or other users' software. Explain it plainly. Do not add deceptive instructions or pretend it provides invisibility.

## Native JSON API

The existing globally bound `SQLite.InsightsRequest(json)` returns JSON. The client attaches the current account ID and verifies it still matches after awaiting. The C# bridge catches errors without sending stack traces/connection strings.

| Action | Input beyond account/action | Result / effect |
| --- | --- | --- |
| status | none | cache metadata only; no creation or source scan |
| create | none | initializes derived file and schema only |
| rebuild | explicit UI confirmation | backs up/replaces generated cache only |
| start | explicit user action | snapshots sources or resumes the existing job |
| step | jobId | one bounded import/derive transaction |
| people | search, friendsOnly | up to 30 cached candidates |
| events | userIds, since, until, filter, page, size | total + current SQL result page |
| summary | userIds, since, until, groupOnly, page, size | member totals + paged overlap pairs |

One query scope accepts 1–200 users. Result pages are limited to 1–100 rows, not 100 rows of retained history. Filters: encounters, join, left, status, bio, all. Random-access event pages use LIMIT/OFFSET on the cache with indexes; very deep offsets can cost time, but do not transfer full history to JS. Do not cap the dataset at five pages; only the visible page-number window is five.

The frontend's one-second account check reads only the in-memory account ID. It is not a database analysis/polling loop. Changing account, leaving the page, switching scope or replacing a request must invalidate stale async results. Pause waits for the current native batch rather than rolling back a committed checkpoint.

## Navigation integration

Register `local-insights` in the normal `navDefinitions` (`src/shared/constants/ui.js`), add default entry at the bottom in `navLayoutDefaults.js`, and supply localization keys. Keep router `meta.navKey` consistent. The original layout manager handles ordering, folders and hidden entries.

Never reinsert `LocalInsightsNav` under SidebarHeader. The script removes old v1/v2 header injection from already-integrated worktrees. Existing dashboards and their widget registries are untouched; this change is a peer navigation page, not a new dashboard widget type.

## Build and verification

From the repository root, use the versions in the lockfile and workflows:

```text
node build-scripts/apply-local-insights.mjs
node build-scripts/apply-local-insights.mjs
node --test tests/local-insights/analytics.test.mjs tests/local-insights/reader.test.mjs tests/local-insights/cache-integration.test.mjs
npm ci
npx vitest run --config tests/local-insights/vitest.config.mjs
npm run prod
dotnet run --project tests/InsightsCache.Edges/InsightsCache.Edges.csproj -c Release
dotnet run --project tests/InsightsCache.Native/InsightsCache.Native.csproj -c Release -- --stress
```

The integration script must be idempotent; compare its resulting source diff before and after the second call. Native tests link the REAL two C# cache implementation files and use actual temporary SQLite databases. The default large test uses 6,001 visits / 120,020 join-leave rows; `--stress` uses 26,001 visits / 520,020 join-leave rows plus Locations and other synthetic events. It checks unchanged-source bytes, checkpoint reopening, cached queries without the source file present, true counts, page 6, incremental idempotence and late imports.

Native Windows app build additionally compiles the actual bridge. Use the dedicated preview workflow; it must run tests, production frontend, native app, installer and asset checks before publishing. `test-results/cache-native.json`, `cache-edges.json`, mounted UI reports, and build metadata are evidence. PASS of a mocked UI test does NOT prove native CEF runtime behavior.

Do not disable a failing assertion or skip the native tests merely to make a Release green. Fix and rerun, inspect the actual job steps, then verify published assets and their tested commit.

## Scope not implemented

No full Jirai port; no inferred relationships, hidden locations, automatic group joins, remote nonfriend polling, automatic following, multiple concurrently logged-in account views, CSV export, partial-history browsing while the first index is incomplete, or verified Electron-native cache bridge. Windows x64 CEF is the release/test target.

Actual installation/upgrade/uninstall coexistence, VR behavior, real user data and interactive CEF verification require local acceptance and must remain NOT_RUN until performed. Never upload real databases, generated personal caches, tokens, cookies or player-identifying screenshots to public repositories.
