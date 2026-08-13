# .hyperreview — review artifact shelf (fork-local)

Fleet index of structured review runs for this fork. Artifacts live on the fork's
`main` (meta surface, per AGENTS.md); PR branches to upstream never carry them.

| Run | Target / ref | Mode | Tier | Verdict | Reviewer | Validation |
| --- | --- | --- | --- | --- | --- | --- |
| `patch-review-2026-08-13T14-20-00Z/` | branch `feat/ble-transport-macos` (dirty worktree vs upstream/main): macOS BLE transport for Niimbot.Net (issue #13) | patch-review | Full (churn >500 lines; 2 high findings) | WARN — F1 leak, F2 use-after-free, F3 callback crash-safety; fixed in-loop post-review, fixes noted in `summary.md` | self/codex-agent (implementing agent) | `result.json` schema-valid (jsonschema, strict profile) |
