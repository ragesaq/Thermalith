#!/usr/bin/env python3
"""Consistency checker for the .agents/ tracking set.

Run before committing changes to any tracking file:
    python3 .agents/check_tracking.py
Exit 0 = consistent; nonzero = fix what it prints. CI-free by design; the
session contract in AGENTS.md is what makes this run.
"""
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ERRORS = []


def err(msg):
    ERRORS.append(msg)


def load(name):
    path = HERE / name
    if not path.exists():
        err(f"{name}: missing")
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        err(f"{name}: invalid JSON — {e}")
        return None


def check_work_map(wm):
    statuses = set(wm.get("statuses", []))
    nodes = wm.get("nodes", [])
    ids = [n.get("id") for n in nodes]
    for dup in {i for i in ids if ids.count(i) > 1}:
        err(f"WORK_MAP: duplicate node id {dup}")
    known = set(ids)
    for n in nodes:
        nid = n.get("id", "<missing id>")
        if not re.fullmatch(r"[a-z0-9]+-\d{3}", nid or ""):
            err(f"WORK_MAP {nid}: id must look like owner-001")
        for field in ("title", "owner", "status", "next", "updated"):
            if not n.get(field):
                err(f"WORK_MAP {nid}: missing/empty '{field}'")
        if n.get("status") not in statuses:
            err(f"WORK_MAP {nid}: status {n.get('status')!r} not in {sorted(statuses)}")
        for need in n.get("needs", []):
            if need not in known:
                err(f"WORK_MAP {nid}: needs unknown node {need!r}")
        if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", n.get("updated") or ""):
            err(f"WORK_MAP {nid}: 'updated' must be YYYY-MM-DD")
    # acyclic needs
    graph = {n["id"]: list(n.get("needs", [])) for n in nodes if n.get("id")}
    state = {}

    def visit(node, stack):
        if state.get(node) == "done":
            return
        if state.get(node) == "visiting":
            err(f"WORK_MAP: dependency cycle involving {' -> '.join(stack + [node])}")
            return
        state[node] = "visiting"
        for dep in graph.get(node, []):
            if dep in graph:
                visit(dep, stack + [node])
        state[node] = "done"

    for node in graph:
        visit(node, [])
    return {n["id"]: n for n in nodes if n.get("id")}


def check_ledger(node_index):
    path = HERE / "WORK_LEDGER.md"
    if not path.exists():
        err("WORK_LEDGER.md: missing")
        return
    text = path.read_text(encoding="utf-8")
    ledger_ids = set(re.findall(r"^- \d{4}-\d{2}-\d{2} — .*\[([a-z0-9]+-\d{3})\]", text, re.M))
    for lid in ledger_ids:
        node = node_index.get(lid)
        if node is None:
            err(f"WORK_LEDGER: line references unknown node {lid}")
        elif node.get("status") != "done":
            err(f"WORK_LEDGER: {lid} has a ledger line but map status is {node.get('status')!r}, not 'done'")
    for nid, node in node_index.items():
        if node.get("status") == "done" and nid not in ledger_ids:
            err(f"WORK_MAP: {nid} is 'done' but has no WORK_LEDGER line")


def check_id_list(data, name, key, pattern):
    items = data.get(key, [])
    ids = [d.get("id") for d in items]
    for dup in {i for i in ids if ids.count(i) > 1}:
        err(f"{name}: duplicate id {dup}")
    for d in items:
        did = d.get("id", "<missing id>")
        if not re.fullmatch(pattern, did or ""):
            err(f"{name} {did}: id must match {pattern}")
        for field in ("title", "date") if name == "DECISIONS" else ("title", "ref"):
            if not d.get(field):
                err(f"{name} {did}: missing/empty '{field}'")


def main():
    goal = load("GOAL.json")
    if goal is not None:
        for field in ("owner", "objective", "successCriteria", "constraints"):
            if not goal.get(field):
                err(f"GOAL {field}: missing/empty")
    wm = load("WORK_MAP.json")
    node_index = check_work_map(wm) if wm is not None else {}
    check_ledger(node_index)
    decisions = load("DECISIONS.json")
    if decisions is not None:
        check_id_list(decisions, "DECISIONS", "decisions", r"D-\d{3}")
    artifacts = load("ARTIFACTS.json")
    if artifacts is not None:
        check_id_list(artifacts, "ARTIFACTS", "artifacts", r"A-\d{3}")

    if ERRORS:
        print("check_tracking: FAIL")
        for e in ERRORS:
            print(f"  - {e}")
        return 1
    print("check_tracking: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
