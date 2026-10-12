#!/usr/bin/env python3
"""Rubbish pace model: the first loop and the first in-game day, before and after the 2026-10-12 pace pass (Docs/Progression.md §3.6).

NOT a port of the game and NOT play-tested. A small Monte Carlo of one load (the Curb Pup's 120 kg of messy-start backlog bags) and
of one in-game day (20 real minutes of loops), for four kinds of player, under the OLD economy and the NEW one. It answers:
  1. Does one full first load earn rank 2 (and when in the load does the promotion land)?
  2. How long until a player can afford Magnus's job ($500, rank 2) AND the Bag Belt ($900, rank 3)?
  3. What does the streak ladder (x1.5 .. x10, tighter windows, cooling) pay a fast, accurate hand sorter next to a slow one?

Where the numbers come from
  - Bag mass, items per bag and the base sort value per bag: the Acres row of Docs/sim/campaign_model.py (a statistical port of
    ItemCatalog.RollBag): 6.88 kg, 8.93 items, $3.59 of recycling + scrap + hazard pay per bag at the OLD rates.
  - Economy / Rep / rank numbers: Economy (Core/RubbishTypes.cs), RepValues (Progression/ProgressionIds.cs), Progression.ranks,
    SortLadder (Sorting/SortLadder.cs) as of 2026-10-12.
  - Player times (seconds per stop, bag and item; drive time) and mistake rates: ASSUMPTIONS in the spirit of Progression.md §8.6.
    "locked in" is a fast, careful player who has done this before; nobody has measured one.

Assumptions that matter (the first review of this file, 2026-10-12, found these were the soft spots): a quarter of a load's sort
value is junk scrap, paid by weight at the compactor with NO streak multiplier; the next bag is fetched while the table still has items,
so only a share of bag boundaries (`gap`) outlast the clock; a mistake drops that item's pay (it is spat back and not re-sorted, which is
conservative).

What it leaves out: treasure (a find is worth a few dollars to a few hundred, pays only when sold on ebag), fresh on-time bags
(day 1 is the old backlog: every bag is at the late floor), Work Orders, midnight pay, raccoons, buying anything mid-run.

Run:  python3 pace_model.py            both tables, 300 runs each
      python3 pace_model.py sens       the same player types with each NEW knob moved
"""
import random
import statistics
import sys

# ------------------------------------------------------------------------------------------------------ economy snapshots
OLD = dict(
    fee=4.0, floor=0.25, curb=2.0, sort_mult=1.0,                 # sort_mult scales recycle / scrap / hazard pay
    rep_bag_late=1.0, rep_curb=2.0, rep_item=0.5, rep_wrong=-2.0,
    r2=450, r3=950,
    # +10% a sort, x2 cap, 2.5 s window, the streak simply resets when the window runs out
    ladder=[(1, 1.0)] + [(n, 1.0 + 0.1 * (n - 1)) for n in range(2, 12)],
    windows=[2.5], cool=False,
)
NEW = dict(
    fee=12.0, floor=0.6, curb=6.0, sort_mult=4.0,
    rep_bag_late=8.0, rep_curb=10.0, rep_item=3.0, rep_wrong=-5.0,
    r2=350, r3=950,
    ladder=[(1, 1.0), (3, 1.5), (6, 2.0), (10, 3.0), (15, 4.0), (22, 5.0), (35, 10.0)],
    windows=[3.0, 2.8, 2.6, 2.4, 2.2, 2.0, 1.8], cool=True,      # each missed window cools one tier instead of ending the streak
)

# Acres backlog, Curb Pup
KG, ITEMS, SORTVAL, CAP_KG = 6.88, 8.93, 3.59, 120.0
BAGS = int(CAP_KG // KG)             # 17
HEAPS = 7                            # the messy start's heaps hold about 2.4 bags
PRICE_MAGNUS, PRICE_BELT = 500.0, 900.0
JUNK_SHARE = 0.25                    # share of a load's sort value that is junk scrap: paid by weight, never multiplied by the streak

PLAYERS = {
    #               s/stop  s/bag  s/item  s depot  drive s   mistakes  P(a bag's fetch outlasts the window)
    "casual":    dict(stop=9.0, bag=6.0, item=2.6, depot=25, drive=280, err=0.10, gap=0.90),
    "typical":   dict(stop=7.0, bag=4.7, item=2.0, depot=15, drive=230, err=0.05, gap=0.55),
    "skilled":   dict(stop=5.0, bag=3.5, item=1.5, depot=10, drive=200, err=0.03, gap=0.30),
    "locked in": dict(stop=4.0, bag=3.0, item=1.2, depot=8,  drive=180, err=0.015, gap=0.12),
}


def tier_start(econ, tier):
    return econ["ladder"][tier][0] if tier < len(econ["ladder"]) else 0


def tier_of(econ, streak):
    t = 0
    for i, (n, _) in enumerate(econ["ladder"]):
        if streak >= n:
            t = i
    return t


def mult_of(econ, streak):
    return econ["ladder"][tier_of(econ, streak)][1]


def window_of(econ, streak):
    w = econ["windows"]
    return w[min(tier_of(econ, streak), len(w) - 1)] if len(w) > 1 else w[0]


def after_idle(econ, streak, idle):
    """What a streak is worth after idle seconds without a correct sort (SortLadder.Cool; the old ladder just resets)."""
    while streak > 0:
        w = window_of(econ, streak)
        if idle <= w:
            return streak
        idle -= w
        if not econ["cool"]:
            return 0
        t = tier_of(econ, streak)
        streak = 0 if t == 0 else econ["ladder"][t - 1][0] if t - 1 > 0 else 0
    return 0


def sort_load(econ, p, rng):
    """Sort one load by hand. Returns cash, Rep, seconds, mistakes, top multiplier, mean multiplier."""
    n_items = int(round(ITEMS * BAGS))
    base_item = SORTVAL * econ["sort_mult"] / ITEMS
    streak, cash, rep, t, wrong, msum, mmax = 0, 0.0, 0.0, 0.0, 0, 0.0, 1.0
    for i in range(n_items):
        dt = rng.lognormvariate(0, 0.28) * p["item"]
        idle = dt                                                 # the time since the last correct sort is this item's time ...
        if i % int(ITEMS) == 0:                                   # ... plus, at a new bag, carrying it over and ripping it
            t += p["bag"] * 0.6
            if rng.random() < p["gap"]:
                idle += rng.uniform(2.5, 6.0)
        t += dt
        streak = after_idle(econ, streak, idle)
        if rng.random() < p["err"]:
            wrong += 1
            streak = 0
            rep += econ["rep_wrong"]
            t += 1.2                                              # spat back: grab it again
            continue
        streak += 1
        m = mult_of(econ, streak)
        cash += base_item * rng.uniform(0.5, 1.5) * (JUNK_SHARE + (1.0 - JUNK_SHARE) * m)
        rep += econ["rep_item"]
        msum += m
        mmax = max(mmax, m)
    return cash, rep, t, wrong, mmax, msum / max(1, n_items)


def collect_load(econ, p):
    pay = BAGS * econ["fee"] * econ["floor"] + (HEAPS - 1) * econ["curb"]
    rep = BAGS * econ["rep_bag_late"] + (HEAPS - 1) * econ["rep_curb"]
    t = p["drive"] + HEAPS * p["stop"] + BAGS * p["bag"] + p["depot"]
    return pay, rep, t


def one_day(econ, p, seed, day_s=1200.0):
    rng = random.Random(seed)
    t = cash = rep = 0.0
    pts = [(0.0, 0.0, 0.0)]                                       # (seconds, cash, rep): piecewise linear between the points
    loads, r2_t, r3_t = 0, None, None
    while t < day_s * 2:                                          # run two days so "when" is always answerable
        pay, r, dt = collect_load(econ, p)
        t += dt; cash += pay; rep += r
        pts.append((t, cash, rep))
        c, r2, dt2, wrong, mmax, mavg = sort_load(econ, p, rng)
        t += dt2; cash += c; rep += r2
        pts.append((t, cash, rep))
        loads += 1

    def cross(idx, target):
        for (t0, *v0), (t1, *v1) in zip(pts, pts[1:]):
            if v0[idx] < target <= v1[idx]:
                return t0 + (t1 - t0) * (target - v0[idx]) / (v1[idx] - v0[idx])
        return None

    def at(sec):
        for (t0, c0, r0), (t1, c1, r1) in zip(pts, pts[1:]):
            if t0 <= sec <= t1:
                f = (sec - t0) / max(1e-9, t1 - t0)
                return c0 + (c1 - c0) * f, r0 + (r1 - r0) * f
        return pts[-1][1], pts[-1][2]

    r2_t, r3_t = cross(1, econ["r2"]), cross(1, econ["r3"])
    k_t = cross(0, PRICE_MAGNUS + PRICE_BELT)
    both_t = max(k_t, r3_t) if k_t is not None and r3_t is not None else None
    day_cash, day_rep = at(day_s)
    done = sum(1 for (tt, _, _) in pts[1::2] if tt <= day_s)       # loads finished inside the day
    return dict(r2=r2_t, r3=r3_t, both=both_t, day=(done, day_cash, day_rep))


def one_load(econ, p, seed):
    rng = random.Random(seed)
    pay, rep_c, t_c = collect_load(econ, p)
    c, rep_s, t_s, wrong, mmax, mavg = sort_load(econ, p, rng)
    return dict(cash=pay + c, rep=rep_c + rep_s, t=t_c + t_s, mavg=mavg, mmax=mmax)


def table(econ, label, runs=300):
    print(f"\n=== {label} ===")
    print(f"{'player':10} | one load: $     Rep  min  | x avg / top | rank 2 at | day 1 (20 min): loads done $       Rep | Magnus + Bag Belt affordable at | rank 3 at")
    for name, p in PLAYERS.items():
        loads = [one_load(econ, p, s) for s in range(runs)]
        days = [one_day(econ, p, 1000 + s) for s in range(runs)]
        med = statistics.median

        def when(key):
            xs = [d[key] for d in days if d[key] is not None]
            return (f"{med(xs) / 60:4.1f} min" if xs else "   --    "), len(xs) / runs

        r2, r2p = when("r2")
        r3, r3p = when("r3")
        both, bothp = when("both")
        dd = [d["day"] for d in days if d["day"]]
        print(f"{name:10} | {med([l['cash'] for l in loads]):7.0f} {med([l['rep'] for l in loads]):5.0f} {med([l['t'] for l in loads]) / 60:4.1f}  | "
              f"{med([l['mavg'] for l in loads]):4.1f} / {med([l['mmax'] for l in loads]):4.1f} | {r2} ({r2p:.0%}) | "
              f"{med([x[0] for x in dd]):4.1f}  {med([x[1] for x in dd]):6.0f} {med([x[2] for x in dd]):6.0f} | {both} ({bothp:.0%})              | {r3} ({r3p:.0%})")


def sensitivity():
    base = dict(NEW)
    for label, kw in [
        ("pickup x2.5 and sorting x3 (fee 10, sort 3)", dict(fee=10.0, sort_mult=3.0)),
        ("pickup x3.5 and sorting x5 (fee 14, sort 5)", dict(fee=14.0, sort_mult=5.0)),
        ("no cooling: a missed window ends the streak", dict(cool=False)),
        ("windows 3.5 s flat (no speed pressure)", dict(windows=[3.5])),
        ("ladder tops out at x5", dict(ladder=[(1, 1.0), (3, 1.5), (6, 2.0), (10, 3.0), (15, 4.0), (22, 5.0)])),
        ("top tier at 25 instead of 35", dict(ladder=[(1, 1.0), (3, 1.5), (6, 2.0), (10, 3.0), (15, 4.0), (22, 5.0), (25, 10.0)])),
        ("Rep per item 2 instead of 3", dict(rep_item=2.0)),
    ]:
        e = dict(base)
        e.update(kw)
        table(e, "NEW, but " + label, runs=150)


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "sens":
        sensitivity()
    else:
        table(OLD, "OLD economy (before 2026-10-12)")
        table(NEW, "NEW economy (2026-10-12 pace pass)")
