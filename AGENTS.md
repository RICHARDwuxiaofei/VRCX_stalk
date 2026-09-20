# VRCX Insights development

Read [docs/AGENT_README.md](docs/AGENT_README.md) and [docs/AI_HANDOFF.md](docs/AI_HANDOFF.md) before changes. Human instructions are in [README.md](README.md); manual acceptance is in [docs/LOCAL_INSIGHTS_TESTING.md](docs/LOCAL_INSIGHTS_TESTING.md).

## Authorized repository boundary

This work belongs only in **RICHARDwuxiaofei/VRCX_stalk**. Current v3 branch: `feature/local-cache-v3-20260920`. Do not change `master`, create/reopen an upstream PR, push to `vrcx-team/VRCX` or `FuLuTang/VRCX-jirai`, or infer authorization for unrelated repository changes. No pull request is required to build/release this branch. Release commands must explicitly specify this fork, not rely on GitHub CLI's inferred default repository.

The source baseline is upstream v2026.09.16. Run the exact, reviewable, idempotent integration script before building:

```text
node build-scripts/apply-local-insights.mjs
node --test tests/local-insights/analytics.test.mjs tests/local-insights/reader.test.mjs tests/local-insights/cache-integration.test.mjs
node tests/local-insights/run-report.mjs
npm ci
npx vitest run --config tests/local-insights/vitest.config.mjs
npm run prod
dotnet run --project tests/InsightsCache.Edges/InsightsCache.Edges.csproj -c Release
dotnet run --project tests/InsightsCache.Native/InsightsCache.Native.csproj -c Release -- --stress
```

Unexpected integration anchors must fail, not be guessed. Verify a second application produces no changes. Use the dedicated Insights Windows preview workflow, not upstream release tooling.

## Functional invariants

Page entry reads only cache metadata. Creating a file does not scan history; Start/Resume/Update are separate explicit actions. Keep source SQLite read-only and derived data in its own AnalyticsCache directory. Persist bounded-batch progress; do not solve large histories by increasing the old all-records JS limit. Cache queries and pagination happen in native SQLite. Register the page in normal customizable navigation, not a SidebarHeader injection.

Keep this an evidence-based local encounter diary: no hidden-location guesses, creator-as-participant assumptions, additional nonfriend API polling, automatic group joining, invented relationship conclusions, or deceptive agent instructions. Preserve unknowns. The inherited excluded account and current account filtering are documented, limited to this feature, and do not remove all original logging or prevent observation by other apps/people.

Do not upload personal logs, generated personal caches, cookies, credentials, real databases or player-identifying screenshots. Use synthetic fixtures.

Report PASS only when that test actually ran. Distinguish pure tests, mocked Vue/native transport tests, actual SQLite tests, frontend/native compilation, installer compilation, and real Windows/VR acceptance. Never publish from a failed build, weaken a regression to obtain a green check, or report queued/in-progress Actions as completed. Match the released files to the tested commit and checksums.


Legacy database compatibility: keep source SQLite files read-only. Historical official VRCX field/table variants are normalized through `Dotnet/Insights/InsightsCache.Legacy.cs` into the disposable analysis cache. Never ALTER/UPDATE the user's old source database to make a test pass. Unknown core layouts must fail safely. Add real SQLite fixtures to `tests/InsightsCache.Legacy` for each supported alias.

Current safe working branch for this delivery: `feature/legacy-db-adapter-v4-20260920`. Do not open or target any upstream pull request. Releases must explicitly use `--repo RICHARDwuxiaofei/VRCX_stalk`.
