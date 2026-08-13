# AGENTS.md — rulebook for AI agents working in this fork

This is **ragesaq's fork** of
[EvilGeniusLabs-ca/Thermalith](https://github.com/EvilGeniusLabs-ca/Thermalith)
(the GitHub mirror of the project's self-hosted GitLab upstream). The fork
exists to **contribute upstream**, not to diverge. First target:
**Bluetooth (BLE) printing as a real transport/driver** for ragesaq's NIIMBOT
printer — upstream issue
[#13](https://github.com/EvilGeniusLabs-ca/Thermalith/issues/13), which specs
exactly this work.

## Identity and git (this fork)

- Commit as `ragesaq <11304287+ragesaq@users.noreply.github.com>` (set
  repo-locally in this clone). Never commit as Caliper, PsiClawOps, or the
  upstream maintainer here.
- **Sign off every commit intended for upstream**: `git commit -s` (DCO —
  upstream requires the `Signed-off-by:` line, see CONTRIBUTING.md).
- Remotes: `origin` = `git@github.com:ragesaq/Thermalith.git` (the fork;
  the default `github.com` SSH host on this machine authenticates as
  ragesaq). `upstream` = `https://github.com/EvilGeniusLabs-ca/Thermalith`.
  **Never push to `upstream`.**

## Branch discipline — keep PRs clean

Fork `main` = upstream `main` **plus** fork-local meta commits (this file,
`.agents/`). Those meta files must never appear in an upstream PR, so:

- **PR branches always start from `upstream/main`**, never from fork `main`:
  `git fetch upstream && git switch -c feat/<topic> upstream/main`.
- One logical change per PR; test suite green (`dotnet test`) before pushing.
- Sync recipe for `main`: `git fetch upstream && git rebase upstream/main`
  (meta commits ride on top), then `git push --force-with-lease origin main`.

## Upstream conventions (follow these in code you'll PR)

- Read `CLAUDE.md` at the repo root for architecture, build/test commands,
  and code conventions — but note it is the **upstream maintainer's** guide
  for *their* environment. Its environment- and identity-specific rules
  (commit-message identity, "app is often running", internal-docs paths) do
  **not** apply in this fork; this file wins on any conflict.
- Never edit `CLAUDE.md` or other upstream-owned files as part of meta work;
  that would create permanent sync conflicts.
- No `---` horizontal rules in markdown (upstream house style).
- Contribution flow: PR against the GitHub mirror → maintainer reviews and
  lands commits into their GitLab upstream with authorship preserved → the
  mirror syncs back and the PR is closed with a pointer. Agents never merge.
- Build: `dotnet build src/Thermalith.App/Thermalith.App.csproj`. Tests:
  `dotnet test`. Real failures are compile errors
  (`grep -iE "error (CS|AVLN|XAMLIL|XFC)"` must print nothing).

## Memory system — `.agents/`

Durable agent memory lives in `.agents/` (append-mostly, plain files, works
identically under Codex and Claude Code). Modeled on the AIStart program's
repo-starter-solo tracking set:

- `.agents/GOAL.json` — why this fork exists; success criteria; constraints.
- `.agents/WORK_MAP.json` — all work in flight. Every node not `done` or
  `parked` has a concrete `next` step written so a **cold session can pick
  it up without any chat history** — this is the handoff mechanism.
- `.agents/DECISIONS.json` — append-only decision log. Never rewrite an
  entry; supersede with a new one.
- `.agents/ARTIFACTS.json` — registry of durable references (specs, issues,
  reference implementations, key source files).
- `.agents/WORK_LEDGER.md` — append-only logbook of finished work, newest
  first: `- YYYY-MM-DD — what was finished [node-id] (commit/PR)`.

**Session contract:** at session start, read `GOAL.json` and `WORK_MAP.json`
(and `WORK_LEDGER.md` for recent history). Before ending any session where
work happened: update the touched map nodes (`status`, `next`, `updated`),
append a ledger line for anything finished, record new decisions — **in the
same commit as the work** — and run `python3 .agents/check_tracking.py`
(must pass).

## Scope guardrails

- Upstream-first: prefer changes the maintainer can land as-is. The BLE
  transport goes behind `INiimbotTransport` (build spec §5.1) with framing,
  commands, and client code untouched.
- GPL-3.0-or-later, inbound = outbound; no code copied from incompatibly
  licensed sources. Porting *logic* from the MIT-licensed reference
  implementations listed in issue #13 is fine; note provenance in commits.
- Hardware tests run on ragesaq's own printer; record model + findings in
  the work map so results survive the session.
