# WORK_LEDGER — logbook of finished work

One line per completed piece of work, newest first. Append a line whenever a
WORK_MAP node reaches `done` — in the same commit that flips the node, so the
ledger and the map always agree (`check_tracking.py` enforces this). Lines
are never edited or removed.

Format: `- YYYY-MM-DD — what was finished [node-id] (PR #n or commit <sha>)`

- 2026-08-13 — Printer identified: NIIMBOT B1 Pro (id 4097, 300 dpi, 567 px, B1 series, catalogue-unverified); wireless channel confirmed as BLE by desk research (SiFli SF32 BLE MCU, niimblue drives it via Web Bluetooth) [ragesaq-002] (commit 4c40b83)
- 2026-08-13 — Fork stood up: ragesaq/Thermalith created, remotes + ragesaq identity set, AGENTS.md rulebook and .agents/ memory system committed and pushed to fork main [ragesaq-001] (commit 66f3d13)
