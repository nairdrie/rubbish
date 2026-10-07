#!/usr/bin/env python3
"""Focused assertions for `nightextra` and `nightkingnatural` bot runs.

Usage: python3 Tools/bots/check_nightx.py RUN_DIR [extras|natural]
The generic run_bots audit must also pass. This script asserts the F4 gameplay receipts it cannot infer.
"""

import json
import sys
from pathlib import Path


def read(path):
    return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]


def run(folder, mode):
    checks = []

    def check(label, okay):
        checks.append((label, bool(okay)))

    logs = {n: read(folder / f"bot-{n}" / "audit.jsonl") for n in (1, 2) if (folder / f"bot-{n}" / "audit.jsonl").exists()}
    if mode == "extras":
        logs[3] = read(folder / "bot-3" / "audit.jsonl")
    host = logs[1]

    def rows(n, kind, **fields):
        return [r for r in logs[n] if r.get("kind") == kind and all(r.get(k) == v for k, v in fields.items())]

    audit_path = folder / "audit.json"
    if audit_path.exists():
        audit = json.loads(audit_path.read_text())
        check("generic bot audit passed", audit.get("ok") is True)
    else:
        check("generic bot audit exists", False)

    if mode == "extras":
        check("two traps placed and caught", len(rows(1, "night.trap", op="place")) >= 2 and len(rows(1, "night.trap", op="catch")) >= 2)
        check("Animal Control bounty resolved", len(rows(1, "night.trap", op="bounty")) == 1)
        check("pet raccoon resolved", len(rows(1, "night.trap", op="pet")) == 1)
        check("exact $40 bounty credited", any(r.get("cents") == 4000 for r in rows(1, "bizledger", id="animal_control", op="credit")) and
              any(r.get("cents") == 4000 for r in rows(2, "ledger", id="animal_control", op="credit")))
        check("guest received pet inventory item", bool(rows(3, "night.item", **{"def": "pet_raccoon"})) and
              any(r.get("pets", 0) >= 1 for r in rows(3, "nightx", op="friend-shot-result")))
        check("friend rescued latch", bool(rows(1, "night.latch", op="end", how="friend")))
        check("Rabies Tag toggled on and off", bool(rows(1, "night.rabiestag.toggle", on=True)) and
              bool(rows(1, "night.rabiestag.toggle", on=False)))
        check("rabid shove attempted and passed infection", len(rows(1, "night.rabiestag")) >= 1 and
              any(r.get("pass") is True for r in rows(1, "night.rabiestag")))
        shots = rows(1, "night.shot")
        check("self and friend shots authorized by host", len(shots) >= 2 and len({r.get("for") for r in shots}) >= 2)
        check("two $35 Rabies Shots charged", len([r for r in rows(1, "bizledger", id="rabies_shot") if r.get("cents") == -3500]) >= 2)
        check("self shot cured guest", any(r.get("rabid") is False for r in rows(2, "nightx", op="self-shot-result")))
        check("friend carried into kiosk", any(r.get("carrying") is True for r in rows(2, "nightx", op="friend-shot-request")))
        check("friend shot cured patient", any(r.get("rabid") is False for r in rows(3, "nightx", op="friend-shot-result")))
        check("all traps resolved", any(r.get("traps") == 0 for r in rows(1, "nightx", op="summary")))
    elif mode == "natural":
        check("guest entered shop", any(r.get("indoors") is True for r in rows(2, "nightx", op="natural-guest-inside")))
        check("real nightly roll was scheduled", bool(rows(1, "night.king.roll")) and bool(rows(1, "nightx", op="natural-pending")))
        check("king did not spawn while everyone was indoors", not rows(1, "nightx", op="natural-fail") and
              all(r.get("indoors") is True for r in rows(1, "nightx", op="natural-pending")))
        check("guest walked outside", any(r.get("indoors") is False for r in rows(2, "nightx", op="natural-guest-outside")))
        check("natural King spawned with host still inside", bool(rows(1, "night.king", op="start")) and
              any(r.get("hostIndoors") is True and r.get("guestOutdoors") is True for r in rows(1, "nightx", op="natural-pass")))
    else:
        raise ValueError("mode must be extras or natural")

    for label, okay in checks:
        print(("PASS" if okay else "FAIL") + " " + label)
    print(f"check_nightx: {sum(ok for _, ok in checks)}/{len(checks)} passed, {sum(not ok for _, ok in checks)} failed")
    return 0 if all(ok for _, ok in checks) else 1


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    sys.exit(run(Path(sys.argv[1]).resolve(), sys.argv[2]))
