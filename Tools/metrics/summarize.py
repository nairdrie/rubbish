#!/usr/bin/env python3
"""Summarise Rubbish playtest metrics (Docs/Metrics.md) into balance tables.

    python3 Tools/metrics/summarize.py <folder or .jsonl files...> [--md out.md] [--csv outdir]

A folder is searched for *.jsonl (friends' "metrics" folders can be unzipped side by side: pass the parent). Each file is one
play session. Nothing here needs the game or Unity: it only reads the log lines.

Sections: sessions, the campaign (heaps, holds, wins), pacing (ranks, districts, purchases), economy (income by source per game
day), pickups, where time goes, performance, errors. Numbers are medians where a median is meaningful, and every table says how
many sessions or events it rests on, so a thin sample looks thin.
"""
import argparse
import csv
import glob
import json
import os
import statistics
import sys
from collections import Counter, defaultdict


def load(paths):
    files = []
    for p in paths:
        if os.path.isdir(p):
            files += sorted(glob.glob(os.path.join(p, "**", "*.jsonl"), recursive=True))
        else:
            files += sorted(glob.glob(p))
    sessions = []
    for f in files:
        events, bad = [], 0
        with open(f, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    events.append(json.loads(line))
                except ValueError:
                    bad += 1          # a line cut short by a crash is expected; the rest of the file is fine
        if events:
            sessions.append({"file": f, "events": events, "bad": bad})
    return sessions


def head(s):
    for e in s["events"]:
        if e.get("e") == "session":
            return e
    return {}


def pid(s):
    for e in s["events"]:
        if e.get("e") == "identity":
            return e.get("pid", "?")
    return "?"


def of(s, name):
    return [e for e in s["events"] if e.get("e") == name]


def minutes(e):
    return e.get("rt", 0) / 60.0


def med(xs):
    xs = [x for x in xs if x is not None]
    return statistics.median(xs) if xs else None


def f1(x, nd=1):
    return "-" if x is None else f"{x:.{nd}f}"


def table(rows, header):
    out = ["| " + " | ".join(header) + " |", "|" + "|".join("---" for _ in header) + "|"]
    for r in rows:
        out.append("| " + " | ".join(str(c) for c in r) + " |")
    return "\n".join(out)


def real_minutes_at(s, gh):
    """Real minutes at which the game clock first reached gh (from the snapshots)."""
    for e in s["events"]:
        if e.get("gh", -1) >= gh:
            return minutes(e)
    return None


# --------------------------------------------------------------------------------------------------- sections

def sessions_section(S):
    rows = []
    for s in S:
        h = head(s)
        snaps = of(s, "snap")
        end = (of(s, "session_end") or [{}])[0]
        last = snaps[-1] if snaps else {}
        fps = [e["fps"] for e in snaps if e.get("fps")]
        rows.append([
            os.path.basename(s["file"])[:24], h.get("app", "?"), h.get("platform", "?"), pid(s),
            f1(end.get("minutes", minutes(s["events"][-1]))), f"{last.get('rank', '-')}", f"{last.get('d', '-')}",
            f"${last.get('cash', 0):,.0f}" if last else "-", f"{last.get('players', 1)}",
            f1(med(fps), 0), sum(e.get("hitches", 0) for e in snaps), end.get("error_count", sum(e.get("errs", 0) for e in snaps)),
            "yes" if end else "NO (crash/kill)",
        ])
    return "## Sessions\n\n" + table(rows, ["file", "build", "platform", "player", "min", "rank", "day", "cash", "players", "median fps", "hitches", ">100ms", "errors", "clean exit"]) + "\n"


def campaign_section(S):
    out = ["## Campaign contracts\n"]
    byd = defaultdict(list)
    for s in S:
        starts = {e["dist"]: e for e in of(s, "contract_start") + of(s, "contract_resume")}
        cleared = {e["dist"]: e for e in of(s, "backlog_cleared")}
        holds = of(s, "hold_start") + of(s, "hold_break") + of(s, "contract_win")
        wins = {e["dist"]: e for e in of(s, "contract_win")}
        for d, st in starts.items():
            c = cleared.get(d)
            w = wins.get(d)
            byd[d].append({
                "file": os.path.basename(s["file"])[:18], "players": st.get("players", 1), "bags": st.get("bags"),
                "clear_min": (minutes(c) - minutes(st)) if c else None,
                "clear_gh": (c["gh"] - st["gh"]) if c else None,
                "attempts": sum(1 for e in of(s, "hold_start") if e.get("dist") == d),
                "breaks": [e for e in of(s, "hold_break") if e.get("dist") == d],
                "win_min": (minutes(w) - minutes(st)) if w else None,
                "win_gh": (w["gh"] - st["gh"]) if w else None,
                "won": bool(w),
            })
    if not byd:
        return "## Campaign contracts\n\nNo contract events yet.\n"
    for d, runs in sorted(byd.items()):
        out.append(f"### {d}  ({len(runs)} session(s), {sum(1 for r in runs if r['won'])} won)\n")
        rows = []
        for r in runs:
            rows.append([r["file"], r["players"], r["bags"], f1(r["clear_min"]), f1(r["clear_gh"]), r["attempts"], len(r["breaks"]),
                         "WON" if r["won"] else "-", f1(r["win_min"]), f1(r["win_gh"])])
        out.append(table(rows, ["session", "players", "old bags", "clear-up min", "clear-up game h", "hold attempts", "breaks", "result", "to win min", "to win game h"]) + "\n")
        brk = [b for r in runs for b in r["breaks"]]
        if brk:
            held = [b.get("held_h", 0) for b in brk]
            hours = Counter(int(b.get("gh", 0) % 24) for b in brk)
            night = sum(v for h, v in hours.items() if h >= 20 or h < 5)
            out.append(f"Hold breaks: {len(brk)}. Hours held before breaking: median {f1(med(held))}, best {f1(max(held))}. "
                       f"{night} of {len(brk)} broke at night (20:00-05:00). By hour of day: " +
                       ", ".join(f"{h:02d}h:{v}" for h, v in sorted(hours.items())) + ".")
            out.append(f"At the break: clean median {f1(med([b.get('clean') for b in brk]), 0)}%, standing bags median {f1(med([b.get('bags') for b in brk]), 0)}, "
                       f"late median {f1(med([b.get('late') for b in brk]), 0)}, oldest bag median {f1(med([b.get('oldest_h') for b in brk]))} h.\n")
    return "\n".join(out)


def pacing_section(S):
    out = ["## Pacing (real minutes into the session)\n"]
    rows = []
    for s in S:
        for e in of(s, "rank"):
            rows.append([os.path.basename(s["file"])[:18], f"rank {e['from']} -> {e['to']}", f1(minutes(e)), e.get("d"), f"${e.get('cash', 0):,.0f}", e.get("players", 1)])
        for e in of(s, "district_unlock"):
            rows.append([os.path.basename(s["file"])[:18], f"UNLOCK {e['dist']}", f1(minutes(e)), e.get("d"), f"${e.get('cash', 0):,.0f}", e.get("players", 1)])
        for e in of(s, "contract_win"):
            rows.append([os.path.basename(s["file"])[:18], f"CONTRACT WON {e['dist']}", f1(minutes(e)), e.get("d"), f"${e.get('cash', 0):,.0f}", e.get("players", 1)])
    rows.sort(key=lambda r: (r[0], float(r[2])))
    out.append(table(rows, ["session", "what", "min", "game day", "cash", "players"]) + "\n")

    bought = defaultdict(list)
    for s in S:
        for e in of(s, "buy"):
            bought[(e["id"], e["lv"])].append((minutes(e), e.get("price", 0), e.get("rank", 0), e.get("cash_after", 0)))
    rows = []
    for (iid, lv), v in sorted(bought.items(), key=lambda kv: med([x[0] for x in kv[1]])):
        rows.append([f"{iid} L{lv}", len(v), f"${med([x[1] for x in v]):,.0f}", f1(med([x[0] for x in v])), f1(med([x[2] for x in v]), 0),
                     f"${med([x[3] for x in v]):,.0f}"])
    out.append("### Purchases (median across sessions)\n")
    out.append(table(rows, ["item", "bought by (sessions)", "price", "median min", "median rank", "cash left"]) + "\n")
    return "\n".join(out)


TIER_MULT = {1: "x1.5", 2: "x2", 3: "x3", 4: "x4", 5: "x5", 6: "x10"}


def streaks_section(S):
    es = [e for s in S for e in of(s, "sort_streak")]
    out = ["## Sort streaks (hand sorts, 3 or more in a row)\n"]
    if not es:
        return out[0] + "none recorded\n"
    by = defaultdict(list)
    for e in es:
        by[e.get("tier", 1)].append(e)
    rows = []
    for t in sorted(by):
        v = by[t]
        rows.append([TIER_MULT.get(t, t), len(v), f"{100 * len(v) / len(es):.0f}%", f1(med([x.get("peak", 0) for x in v]), 0),
                     f"{100 * sum(1 for x in v if x.get('end') == 'wrong') / len(v):.0f}%"])
    out.append(table(rows, ["best tier reached", "streaks", "share", "median length", "ended by a wrong bin"]) + "\n")
    return "\n".join(out)


def economy_section(S):
    inc, exp, rep = Counter(), Counter(), Counter()
    days = 0.0
    for s in S:
        snaps = of(s, "snap")
        for e in snaps:
            for k, v in (e.get("c") or {}).items():
                if k.startswith("inc."):
                    inc[k[4:]] += v
                elif k.startswith(("exp.", "spend.")):
                    exp[k.split(".", 1)[1]] += v
                elif k.startswith("rep."):
                    rep[k[4:]] += v
        if snaps:
            days += max(0.0, (snaps[-1].get("gh", 0) - snaps[0].get("gh", 0)) / 24.0)
    out = ["## Economy (all sessions together)\n", f"Game days observed: {days:.1f}\n"]
    tot = sum(inc.values()) or 1.0
    out.append("### Income by source\n")
    out.append(table([[k, f"${v:,.0f}", f"{100 * v / tot:.0f}%", f"${v / max(days, 0.01):,.0f}"] for k, v in inc.most_common()],
                     ["source", "total", "share", "per game day"]) + "\n")
    out.append("### Spending by source\n")
    out.append(table([[k, f"${v:,.0f}", f"${v / max(days, 0.01):,.0f}"] for k, v in exp.most_common()], ["source", "total", "per game day"]) + "\n")
    out.append("### Rep by source\n")
    out.append(table([[k, f"{v:,.0f}", f"{v / max(days, 0.01):,.0f}"] for k, v in rep.most_common()], ["source", "total", "per game day"]) + "\n")
    return "\n".join(out)


def pickups_section(S):
    by = defaultdict(list)
    for s in S:
        for e in of(s, "bag"):
            by[e.get("dist", "?")].append(e)
    rows = []
    for d, es in sorted(by.items()):
        n = len(es)
        rows.append([d, n, f"{100 * sum(1 for e in es if e.get('on')) / n:.0f}%", f1(med([e.get("age") for e in es])),
                     f"{100 * sum(1 for e in es if e.get('arm')) / n:.0f}%", f"{100 * sum(1 for e in es if e.get('rac')) / n:.0f}%",
                     f"${sum(e.get('pay', 0) for e in es) / n:.2f}", f"{100 * sum(1 for e in es if e.get('heap')) / n:.0f}%"])
    return "## Pickups\n\n" + table(rows, ["district", "bags", "on time", "median age h", "by arm", "by raccoon crew", "mean pay", "old heap bags"]) + "\n"


def time_section(S):
    act = Counter()
    for s in S:
        for e in of(s, "snap"):
            for k, v in (e.get("act") or {}).items():
                act[k] += v
    tot = sum(act.values()) or 1.0
    return "## Where the time goes\n\n" + table([[k, f"{v / 60:.1f} min", f"{100 * v / tot:.0f}%"] for k, v in act.most_common()], ["HUD mode", "time", "share"]) + "\n"


def perf_section(S):
    bins = [0] * 9
    worst = 0.0
    for s in S:
        for e in of(s, "snap"):
            for i, v in enumerate(e.get("ft") or []):
                if i < 9:
                    bins[i] += v
            worst = max(worst, e.get("ms_max", 0))
    total = sum(bins) or 1
    labels = ["<8.4", "8.4-11.2", "11.2-16.8", "16.8-22.4", "22.4-33.4", "33.4-50", "50-100", "100-250", ">=250"]
    rows = [[l, v, f"{100 * v / total:.1f}%"] for l, v in zip(labels, bins)]
    gpus = Counter(head(s).get("gpu", "?") for s in S)
    return ("## Performance (frame times in ms)\n\n" + table(rows, ["frame ms", "frames", "share"]) +
            f"\n\nWorst single frame: {worst:.0f} ms. GPUs: " + ", ".join(f"{g} x{n}" for g, n in gpus.most_common()) + "\n")


def errors_section(S):
    c = Counter()
    where = defaultdict(set)
    for s in S:
        end = (of(s, "session_end") or [{}])[0]
        for er in end.get("errors", []):
            c[er["sig"]] += er["n"]
            where[er["sig"]].add(os.path.basename(s["file"])[:18])
        if not end:
            for er in of(s, "err"):
                sig = f"{er.get('kind')}|{er.get('msg', '')[:90]}"
                c[sig] += 1
                where[sig].add(os.path.basename(s["file"])[:18])
    rows = [[n, len(where[sig]), sig[:150]] for sig, n in c.most_common(25)]
    return "## Errors (top signatures)\n\n" + (table(rows, ["count", "sessions", "signature"]) if rows else "None logged.") + "\n"


def write_csv(S, outdir):
    os.makedirs(outdir, exist_ok=True)
    with open(os.path.join(outdir, "snaps.csv"), "w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["session", "pid", "rt", "gh", "day", "cash", "rank", "rep", "players", "mode", "fps", "dist", "clean", "bags", "late", "neglected", "oldest_h", "trust", "heap"])
        for s in S:
            name = os.path.basename(s["file"])
            p = pid(s)
            for e in of(s, "snap"):
                for d in e.get("dist") or [{}]:
                    w.writerow([name, p, e.get("rt"), e.get("gh"), e.get("d"), e.get("cash"), e.get("rank"), e.get("rep"), e.get("players"),
                                e.get("mode"), e.get("fps"), d.get("id"), d.get("cl"), d.get("bags"), d.get("late"), d.get("neg"), d.get("old"), d.get("tr"), d.get("hp")])
    print(f"wrote {outdir}/snaps.csv")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="+")
    ap.add_argument("--md", help="write the report here as well as printing it")
    ap.add_argument("--csv", help="also write snaps.csv (one row per district per snapshot) into this folder")
    ap.add_argument("--no-editor", action="store_true", help="leave out sessions played in the Unity Editor")
    a = ap.parse_args()
    S = load(a.paths)
    if a.no_editor:
        S = [s for s in S if not head(s).get("editor")]
    if not S:
        print("no metrics found", file=sys.stderr)
        return 1
    bad = sum(s["bad"] for s in S)
    parts = [f"# Rubbish playtest metrics\n\n{len(S)} session(s), {sum(len(s['events']) for s in S):,} events" + (f", {bad} damaged line(s) skipped" if bad else "") + ".\n",
             sessions_section(S), campaign_section(S), pacing_section(S), economy_section(S), streaks_section(S), pickups_section(S), time_section(S), perf_section(S), errors_section(S)]
    text = "\n".join(parts)
    print(text)
    if a.md:
        with open(a.md, "w", encoding="utf-8") as fh:
            fh.write(text)
    if a.csv:
        write_csv(S, a.csv)
    return 0


if __name__ == "__main__":
    sys.exit(main())
