#!/usr/bin/env python3
"""Check that every bot starts a relayed fling at the rider's exact world point.

Usage: python3 Tools/bots/check_ride_anchor.py RUN_DIR
Run the generic Tools/bots/audit.py separately for state, error and save checks.
"""

import json
import math
import sys
from pathlib import Path


def key(row):
    return int(row["rider"]), round(float(row["tF"]), 4)


def point(row):
    parts = row["start"].split(",")
    if len(parts) != 3:
        raise ValueError(f"bad start point: {row['start']!r}")
    return tuple(float(part) for part in parts)


def distance(a, b):
    return math.dist(point(a), point(b))


def check(folder):
    logs = {}
    for path in sorted(folder.glob("bot-*/audit.jsonl")):
        logs[path.parent.name] = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
    if len(logs) < 2 or "bot-1" not in logs:
        raise ValueError("expected host and at least one guest audit.jsonl")

    hostflings = [row for row in logs["bot-1"] if row.get("kind") == "ride" and row.get("op") == "hostfling"]
    accepted = [row for row in hostflings if row.get("ok") is True]
    failures = []
    if not accepted:
        failures.append("no host-accepted flings")
    for row in hostflings:
        if row.get("ok") is not True:
            failures.append(f"host rejected fling {key(row)}: {row.get('why')}")
        elif row.get("anchorAdjusted") is not False:
            failures.append(f"host adjusted owner's anchor {key(row)}")

    owner = {}
    observers = {}
    for bot, rows in logs.items():
        for row in rows:
            if row.get("kind") != "ride":
                continue
            if row.get("op") == "fling" and row.get("local") is True:
                owner.setdefault(key(row), []).append((bot, row))
            elif row.get("op") == "flingseen" and row.get("local") is False:
                observers.setdefault((bot, key(row)), []).append(row)

    max_gap = 0.0
    max_late = 0.0
    checked = 0
    for event in accepted:
        event_key = key(event)
        sources = owner.get(event_key, [])
        if len(sources) != 1:
            failures.append(f"{event_key}: expected one owner fling, got {len(sources)}")
            continue
        owner_bot, source = sources[0]
        for bot in logs:
            if bot == owner_bot:
                continue
            seen = observers.get((bot, event_key), [])
            if len(seen) != 1:
                failures.append(f"{event_key}: {bot} saw {len(seen)} fling starts")
                continue
            gap = distance(source, seen[0])
            max_gap = max(max_gap, gap)
            max_late = max(max_late, float(seen[0].get("late", 0.0)))
            checked += 1
            if gap > 0.01:
                failures.append(f"{event_key}: {bot} started {gap:.3f} m from owner")

    for failure in failures:
        print("FAIL", failure)
    print(f"check_ride_anchor: {len(accepted)} accepted, {checked} peer starts, "
          f"max spatial gap {max_gap:.3f} m, max observed late {max_late:.3f} s, {len(failures)} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    try:
        sys.exit(check(Path(sys.argv[1]).resolve()))
    except (OSError, ValueError, KeyError) as error:
        print(f"check_ride_anchor: {error}", file=sys.stderr)
        sys.exit(2)
