# VRCX Insights Preview v2

Unofficial Windows x64 preview based on the current `VRCX_stalk` Local Insights branch. **Not a full Jirai port.**

This v2 preview adds the requested Local Insights follow-up:
- 24-hour / 7-day / 30-day / custom / permanent ranges
- type-to-search player autocomplete with direct selection
- Person and Group review modes with locally saved groups
- group-internal pairwise shared-session review
- encounter history with 10-row preview, +20 expansion, then page navigation and page-size selection
- activity logs with All / Join room / Leave room / Status change / Bio change filters
- SQLite datetime normalization to prevent recent records from being dropped by 24-hour filtering

Uses locally stored VRCX data for this review page. Co-presence is observational evidence, not proof of interaction. Missing activity remains unknown.

Windows isolation is preserved: VRCX-Insights.exe, Program Files/VRCX-Insights, %APPDATA%/VRCX-Insights, separate startup/uninstall/IPC identifiers and overlay endpoint. It does not replace the official VRCX installation. Upstream automatic updates are disabled; updates are manual.

The app and installer are **unsigned**. This is a **prerelease** for manual testing. Publication is gated on analysis/database-adapter tests, mounted Vue component tests, production frontend build, native Windows build and installer compilation.

The exact applied integration diff, test outputs and SHA256 checksums are included with the release assets.

The older Jirai relationship subsystem (TwoPersonRelationship / ManualRelations / tracked non-friends and related relationship-graph logic) is **not yet ported** into this preview.
