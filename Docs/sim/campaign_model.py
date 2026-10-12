#!/usr/bin/env python3
"""Rubbish campaign model: expected-value pacing of the six district contracts (Docs/Campaign.md).

NOTE (2026-10-12): the pay and Rep constants below, and the upgrade timings they give, are the economy BEFORE the pace pass (cash x3-4,
Rep x3-6; Docs/Progression.md section 3.6). Re-read them against `pace_model.py` before trusting a campaign length from this file.

NOT a port of the game and NOT play-tested. It is a small expected-value model, one in-game day per step, that
answers five questions for the campaign proposal:
  1. Can a haul-rate threshold alone lock a district?                      (tiers)
  2. How long does each district take a casual / typical / skilled solo player?   (solo)
  3. Does the "locked filth" rule really stop a player who lacks the key item?    (nokey)
  4. What do 2-4 businesses, or one business with hired hands, do to pacing?      (coop)
  5. Which knobs move the campaign length?                                 (sens, prices, path)

Where the numbers come from
  - District rows, pay windows, caps, truck capacities, upgrade prices and rank gates: read from the code on
    2026-10-09 (Progression/Districts.cs, Shop/ScrapazonCatalog.cs, Progression/UpgradeEffects.cs,
    Progression/TruckProgression.cs, Trash/Dumpsters/DumpsterRules.cs).
  - Distances: lane routing over the published StreetGraph and HouseDirectory (depot lot 0 to each stop).
  - Bag mass, items, treasure value: a statistical port of ItemCatalog.RollBag (150,000 bags a district).
  - Player time assumptions (seconds per stop / bag / sorted item, share of the day worked): Docs/Progression.md
    section 8.6. They were assumptions there too; nobody has measured them in play.
  - Everything in the District rows after "retainer", and PUTOUT/grant/bounty rules, is the PROPOSAL.

What it leaves out
  - The TownWorkDirector (the game's dynamic put-out rate) is held at 1.0.
  - The Hoverhog is only "the thing that removes the Bluffs' locked filth"; it is not flown as a vehicle.
  - Raccoons, rabies, the raccoon crew, Blueprints (gloves, boots, backpack), work-order variety, the casino.
  - Showdowns are a win chance per night (casual 60 %, typical 85 %, skilled 95 %: assumed).
  - The buying policy is "key item first, then the cheapest listed upgrade": one plausible path, not the best one.
  - Days are whole steps, so results move in jumps of a day. Read them as "about", never to the decimal.

Run:  python3 campaign_model.py                 every table
      python3 campaign_model.py solo            one table: tiers | solo | nokey | coop | sens | prices | path
      python3 campaign_model.py log typical 1   the day-by-day log (player type, businesses online)
"""
import math
import sys
from dataclasses import dataclass, field, replace

DAY = 1200.0          # real seconds in an in-game day
HOUR = 50.0           # real seconds in an in-game hour

# ----------------------------------------------------------------------------------------------------------------
# Player time assumptions (Docs/Progression.md 8.6). "boss" = chance to win a showdown on a given night (assumed).
# ----------------------------------------------------------------------------------------------------------------
PLAYERS = {
    "casual":  dict(work=0.70, stop=9.0, bag=6.0, item=2.6, depot=25.0, tells=0.30, ebag=0.81, orders=1.2, boss=0.60),
    "typical": dict(work=0.80, stop=7.0, bag=4.7, item=2.0, depot=15.0, tells=0.60, ebag=0.97, orders=2.0, boss=0.85),
    "skilled": dict(work=0.88, stop=5.0, bag=3.5, item=1.5, depot=10.0, tells=0.80, ebag=0.995, orders=2.7, boss=0.95),
}

# Put-out multiplier by businesses online (Docs/Multiplayer.md 13.7).
PUTOUT_MULT = {1: 1.0, 2: 1.7, 3: 2.3, 4: 2.8}


@dataclass
class District:
    key: str
    name: str
    houses: int          # collection stops in the published town
    bags: float          # bags put out a day at full subscription, one business online, work director at 1.0
    kg: float            # mean bag mass (with bag tells)
    items: float         # mean items per bag
    fee: float
    tip: float           # on-time only
    curb: float          # curb-clean bonus
    on_h: float          # on-time window, in-game hours
    floor_h: float       # age at which pay reaches the floor
    floor_pay: float     # pay fraction at the floor
    loop: float          # metres of driving to pass every stop once (nearest-neighbour tour minus the trip there)
    trip: float          # metres, depot -> district -> depot (mean stop, lane routing)
    treasure: float      # treasure appraisal EV per bag ($)
    valuables: float     # valuables per bag
    sortval: float       # non-treasure sort value per bag at base rates ($)
    capcurb: float       # district curb cap (records)
    retainer: float      # Clean Streets max ($ a midnight)
    # ---- CAMPAIGN DESIGN (proposal) ----
    threshold: float = 0.8
    hold: int = 2
    key_item: str = ""
    filth0: float = 200.0      # filth units at the start (1 unit = one old bag's worth)
    locked: float = 0.4        # share of filth0 only the key item can remove
    locked_t: float = 4.0      # seconds per locked unit with the key item (machine time, not skill)
    locked_hand: bool = False  # True when the key item is hand work (Acres furniture): scales with the player
    bounty: float = 2.0        # $ per filth unit removed (city bounty)
    sub0: float = 0.55         # share of households that are customers at 0 % clean (today: 55 % at unlock)
    dump: float = 0.50         # share of the non-customers' trash GoldStar dumps back on the heaps each night
    old_loot: float = 0.20     # treasure in backlog bags, as a share of a fresh bag's
    grant: tuple = (0.10, 0.25, 0.0)   # city grant at 25 / 50 / 75 % clean, as a share of the key price
    win_bonus: float = 1.0     # contract bonus as a multiple of the key price
    nokey_supply: float = 1.0  # share of the put-out reachable without the key item (Commons dumpsters: 0.4)
    key_bag_mult: float = 1.0  # per-bag loading time multiplier once the key item is owned (dumpster tipping)
    butler: bool = False       # Bluffs: two drops a day, 100 s on-time window


# District rows. Everything down to "retainer" is the game's (Districts.cs, the published HouseDirectory and
# StreetGraph, a statistical port of RollBag); the keyword arguments are the proposal.
DISTRICTS = [
    District("acres", "Mildly Pleasant Acres", 37, 35, 6.88, 8.93, 4, 0, 2, 4.0, 12.0, 0.25, 2000, 710,
             7.50, 0.155, 3.59, 60, 60,
             threshold=0.70, hold=1, key_item="truck_model", filth0=80, locked=0.35, locked_t=11.0, locked_hand=True,
             bounty=2.5, sub0=0.70),
    District("meadows", "Minivan Meadows", 22, 30, 7.76, 9.77, 15, 0, 8, 3.5, 10.0, 0.20, 800, 1555,
             31.72, 0.210, 4.59, 50, 250,
             threshold=0.75, hold=2, key_item="truck_arm1", filth0=120, locked=0.40, locked_t=4.2, bounty=4),
    District("commons", "Coupon Commons", 9, 47, 8.00, 9.74, 25, 5, 18, 3.5, 9.0, 0.15, 1250, 1167,
             28.59, 0.204, 4.67, 48, 500,
             threshold=0.80, hold=2, key_item="dunker1", filth0=150, locked=0.45, locked_t=2.5, bounty=6,
             nokey_supply=0.40, key_bag_mult=0.55),
    District("hills", "Snootington Hills", 14, 24, 9.49, 10.53, 40, 15, 30, 3.0, 8.0, 0.10, 1400, 1784,
             145.73, 0.240, 5.52, 40, 900,
             threshold=0.80, hold=3, key_item="truck_arm2", filth0=170, locked=0.40, locked_t=3.0, bounty=10),
    District("heights", "Stackington Heights", 8, 26, 9.12, 10.54, 55, 20, 45, 2.75, 7.0, 0.05, 850, 1996,
             150.02, 0.246, 5.83, 44, 1150,
             threshold=0.85, hold=3, key_item="truck_arm3", filth0=190, locked=0.45, locked_t=2.0, bounty=14),
    District("bluffs", "Billionaire Bluffs", 8, 20, 10.52, 11.44, 80, 40, 80, 2.0, 6.0, 0.0, 1100, 2761,
             661.53, 0.311, 5.97, 34, 1500,
             threshold=0.90, hold=3, key_item="hoverhog", filth0=160, locked=0.50, locked_t=8.0, bounty=20,
             butler=True),
]


# ----------------------------------------------------------------------------------------------------------------
# Upgrades. (id, price, tier, apply). tier = the district (1-6) in which it becomes buyable under the proposal
# (rank r needs ceil(r/2)-1 contracts, so rank r belongs to tier ceil(r/2)).
# ----------------------------------------------------------------------------------------------------------------
@dataclass
class Gear:
    rear: int = 0        # 0 Curb Pup (120 kg), 1 rear-loader (200 kg)
    cap: int = 0         # hopper extensions 0-4
    turbo: int = 0       # 0-3
    arm: int = 0         # 0-3
    tipper: int = 0
    dunker: int = 0      # 0-2
    hog: int = 0         # Hoverhog owned
    belt: int = 0        # conveyor 0-3 (3 = junk diverter)
    table: int = 0       # 0-2
    compactor: int = 0   # 0-3
    recycling: int = 0   # 0-4
    brain: int = 0       # Magnus brain 0-4
    rail: int = 0
    gripper: int = 0     # 0-2
    vision: int = 0      # 0-2
    ebag: int = 0        # 0-3
    owned: set = field(default_factory=set)

    @property
    def capacity(self):
        return (200 if self.rear else 120) + (0, 40, 80, 120, 180)[self.cap]

    @property
    def speed(self):
        return (10.0, 12.5, 15.0, 17.0)[self.turbo]


def _set(attr, val):
    def f(g):
        setattr(g, attr, max(getattr(g, attr), val))
    return f


# Price and rank are today's catalog; the tier is the proposal's (two ranks per district).
CATALOG = [
    # id               price   tier  effect                    note
    ("truck_model",      600,  1, _set("rear", 1)),            # KEY 1  rear-loader
    ("truck_capacity1",  100,  1, _set("cap", 1)),
    ("magnus_brain1",    500,  1, _set("brain", 1)),
    ("truck_capacity2",  450,  1, _set("cap", 2)),
    ("truck_turbo1",     300,  1, _set("turbo", 1)),
    ("truck_handling1",  250,  1, lambda g: None),             # prereq of the arm
    ("truck_arm1",      1800,  2, _set("arm", 1)),             # KEY 2
    ("conveyor1",        900,  2, _set("belt", 1)),
    ("recycling1",       300,  2, _set("recycling", 1)),
    ("auto_tipper",      600,  2, _set("tipper", 1)),
    ("magnus_rail",      300,  2, _set("rail", 1)),
    ("truck_capacity3", 1200,  2, _set("cap", 3)),
    ("sorting_table1",   700,  2, _set("table", 1)),
    ("compactor1",       900,  2, _set("compactor", 1)),
    ("ebag1",           1200,  2, _set("ebag", 1)),
    ("magnus_gripper1", 1200,  2, _set("gripper", 1)),
    ("truck_turbo2",    2800,  2, _set("turbo", 2)),
    ("dunker1",         2400,  3, _set("dunker", 1)),          # KEY 3
    ("truck_capacity4", 3000,  3, _set("cap", 4)),
    ("recycling2",      1500,  3, _set("recycling", 2)),
    ("magnus_vision1",  4000,  3, _set("vision", 1)),
    ("conveyor2",       1200,  3, _set("belt", 2)),
    ("compactor2",      2500,  3, _set("compactor", 2)),
    ("sorting_table2",  6000,  3, _set("table", 2)),
    ("truck_arm2",      9000,  4, _set("arm", 2)),             # KEY 4
    ("magnus_brain2",  12000,  4, _set("brain", 2)),
    ("recycling3",      4000,  4, _set("recycling", 3)),
    ("ebag2",           9000,  4, _set("ebag", 2)),
    ("truck_turbo3",   11000,  4, _set("turbo", 3)),
    ("dunker2",         9000,  4, _set("dunker", 2)),
    ("conveyor3",      14000,  4, _set("belt", 3)),
    ("magnus_vision2", 18000,  4, _set("vision", 2)),
    ("truck_arm3",     39000,  5, _set("arm", 3)),             # KEY 5
    ("magnus_brain3",  40000,  5, _set("brain", 3)),
    ("magnus_gripper2",16000,  5, _set("gripper", 2)),
    ("compactor3",      8000,  5, _set("compactor", 3)),
    ("ebag3",          29000,  5, _set("ebag", 3)),
    ("hoverhog",       85000,  6, _set("hog", 1)),             # KEY 6
    ("recycling4",     11500,  6, _set("recycling", 4)),
    ("magnus_brain4",  60000,  6, _set("brain", 4)),
]
PRICE = {c[0]: c[1] for c in CATALOG}
PREREQ = {"truck_arm1": ["truck_handling1"], "truck_arm2": ["truck_arm1"], "truck_arm3": ["truck_arm2", "truck_capacity4"],
          "dunker1": ["truck_model"], "hoverhog": ["truck_arm2"]}

# Key-item price overrides for every report (empty = today's catalog prices). See report_prices().
KEY_PRICES = {}


# ----------------------------------------------------------------------------------------------------------------
# Time model
# ----------------------------------------------------------------------------------------------------------------
def load_times(g, pl):
    """(seconds per stop, seconds per bag) for curb bags."""
    if g.arm >= 3:
        return 2.0, 0.9
    if g.arm == 2:
        return 4.2, 1.6
    if g.arm == 1:                       # right side only: about 60 % of stops (the old sim's share)
        return 0.6 * 5.0 + 0.4 * pl["stop"], 0.6 * 2.5 + 0.4 * pl["bag"]
    return pl["stop"], pl["bag"]


def trip_time(d, g, pl):
    """One depot round trip: drive there and back, drive in, tip, drive out."""
    tip = 3.0 if g.tipper else 10.0
    return d.trip / (0.85 * g.speed) + 10.0 + 16.0 + tip + pl["depot"]


def collect_time(d, n, g, pl, hands=1, key=False):
    """Seconds to collect n curb bags in district d over the day. hands = people working the same truck."""
    if n <= 0:
        return 0.0
    sweeps = 3 if g.arm >= 2 else 2
    per_stop, per_bag = load_times(g, pl)
    if g.arm < 2:                        # extra hands only help hand loading
        k = min(hands, 3)
        per_stop, per_bag = per_stop / (1 + 0.5 * (k - 1)), per_bag / k
    if key:
        per_bag *= d.key_bag_mult
    visited = 1.0 - math.exp(-n / (d.houses * sweeps))          # share of stops with trash on a sweep
    stops = sweeps * d.houses * visited
    loop_t = d.loop * min(1.0, 0.25 + 0.75 * visited) / (0.6 * g.speed)
    trips = max(1.0, n * d.kg / g.capacity)
    return sweeps * loop_t + stops * per_stop + n * per_bag + trips * trip_time(d, g, pl)


def max_collect(d, supply, g, pl, budget, hands=1, key=False):
    """Most curb bags (<= supply) collectable inside the time budget."""
    if budget <= 0 or supply <= 0:
        return 0.0
    if collect_time(d, supply, g, pl, hands, key) <= budget:
        return supply
    lo, hi = 0.0, supply
    for _ in range(30):
        mid = (lo + hi) / 2
        if collect_time(d, mid, g, pl, hands, key) <= budget:
            lo = mid
        else:
            hi = mid
    return lo


def open_unit_time(d, g, pl, hands=1):
    """Seconds per open backlog unit (a bag off a heap), including its share of depot trips."""
    per_bag = load_times(g, pl)[1]
    if g.arm < 2:
        per_bag = per_bag / min(hands, 3)
    return per_bag + d.kg / g.capacity * trip_time(d, g, pl)


def locked_unit_time(d, g, pl, hands=1):
    t = d.locked_t
    if d.locked_hand:
        t = t * pl["bag"] / 4.7 / min(hands, 3)
    return t + d.kg / g.capacity * trip_time(d, g, pl)


NIGHT_PAY = 1.0 + 0.5 * 0.35       # night pickups pay x1.5; about 35 % of a working day is night
CURB_W = 1.5                       # filth units per customer bag left on the curb overnight


def pay_fraction(d, sweeps):
    """Mean pay fraction and on-time share when bags wait uniformly 0..24/sweeps hours."""
    if d.butler:                    # two drops a day; you are either there for the drop or you are late
        return 0.70, 0.55
    span = 24.0 / sweeps
    n, tot, on = 200, 0.0, 0.0
    for i in range(n):
        a = (i + 0.5) / n * span
        if a <= d.on_h:
            f = 1.0
            on += 1
        elif a >= d.floor_h:
            f = d.floor_pay
        else:
            f = 1.0 - (1.0 - d.floor_pay) * (a - d.on_h) / (d.floor_h - d.on_h)
        tot += f
    return tot / n, on / n


def magnus_bags(d, g):
    """Bags a day Magnus sorts by himself."""
    if g.brain == 0:
        return 0.0
    rate = (0, 8.0, 12.0, 18.0, 30.0)[g.brain] * (1.25 if g.rail else 1.0) * (1.4 if g.gripper >= 2 else 1.0)
    minutes = 20.0 if g.brain >= 4 else 16.0 * HOUR / 60.0
    return rate * minutes / d.items


def sort_time(d, g, pl):
    """Seconds for a player to hand-sort one bag."""
    walk = 0.0 if g.belt >= 1 else 9.0
    items = d.items * (0.67 if g.belt >= 3 else 1.0)
    return walk + 2.0 + items * pl["item"]


def treasure_share(q, v, tells):
    """Share of the treasure captured when a fraction q of the bags is sorted and tells spot a share of treasure bags."""
    q = max(0.0, min(1.0, q))
    if q >= 1.0:
        return 1.0
    if q <= tells * v:
        return q / v if v > 0 else 0.0
    return min(1.0, tells + (q - tells * v) * (1.0 - tells) / (1.0 - tells * v))


def bag_values(d, g, pl):
    """($ non-treasure when sorted, $ treasure realised per bag, $ when crushed)."""
    mult = (1.0, 1.5, 2.0, 3.0, 4.0)[g.recycling]
    sorted_val = d.sortval * mult * 1.15
    fee = (0.08, 0.05, 0.03, 0.01)[g.ebag]
    treasure = d.treasure * pl["ebag"] * (1.0 - fee)
    rate = (0.20, 0.25, 0.30, 0.35)[g.compactor]
    crushed = d.kg * rate + (0.30 * treasure if g.compactor >= 3 else 0.0)
    return sorted_val, treasure, crushed


# ----------------------------------------------------------------------------------------------------------------
# Campaign simulation
# ----------------------------------------------------------------------------------------------------------------
@dataclass
class Result:
    rows: list
    days: float
    log: list
    buys: list = field(default_factory=list)      # (district, day in district, item, price)


def simulate(player="typical", businesses=1, hands=1, max_days=300, tweak=None, verbose=False,
             filth_scale=1.0, price_scale=1.0, key_prices=None, deny_key=None, stop_after=None):
    """One business's campaign.

    businesses = businesses online in the Town (symmetric: each has its own truck and the same gear).
    hands      = people working in THIS business (owner + hired hands sharing one truck).
    deny_key   = index of a district whose key item is never bought (the lock test).
    """
    pl = PLAYERS[player]
    dists = [replace(d) for d in DISTRICTS]
    if tweak:
        tweak(dists)
    mult = PUTOUT_MULT[businesses]
    price = dict(PRICE)
    if key_prices:
        price.update(key_prices)
    price = {k: v * price_scale for k, v in price.items()}
    g = Gear()
    cash = 0.0
    T_person = DAY * pl["work"]
    rows, log, buys = [], [], []
    day = 0                        # whole simulated days
    lost = 0.0                     # expected nights lost to failed showdowns so far (a fraction: not rounded up)
    yesterday_net = 0.0
    won = []
    for di, d in enumerate(dists):
        tier = di + 1
        F0 = d.filth0 * filth_scale
        f_locked = F0 * d.locked
        f_open = F0 - f_locked
        f_curb = 0.0
        streak = 0
        entered = day
        entered_at = day + lost    # calendar day (with showdown retries) on which this district opened
        key_day = thr_day = None
        clean_at_key = None
        granted = [False, False, False]
        net_first = None
        income_sum = 0.0
        best_clean = 0.0
        denied = deny_key == di
        while True:
            day += 1
            in_d = day - entered
            if day > max_days or (denied and in_d > 30) or (stop_after and di > stop_after):
                rows.append(dict(d=d, entered=entered, key=key_day, thr=thr_day, won=None, cash=cash,
                                 net_first=net_first, net_last=yesterday_net,
                                 mean_net=income_sum / max(1, in_d - 1), best_clean=best_clean,
                                 clean_at_key=clean_at_key))
                return Result(rows, float("inf"), log, buys)
            clean = max(0.0, 1.0 - (f_open + f_locked + CURB_W * f_curb) / F0)
            best_clean = max(best_clean, clean)
            sub = d.sub0 + (1.0 - d.sub0) * best_clean     # customers: only ever ratchets up

            # ---- morning: buy ----
            def buy(cid):
                nonlocal cash
                ent = next(c for c in CATALOG if c[0] == cid)
                cash -= price[cid]
                ent[3](g)
                g.owned.add(cid)
                buys.append((d.key, day - entered, cid, price[cid]))

            have_key = d.key_item in g.owned
            if not have_key and not denied:
                for pid in PREREQ.get(d.key_item, []) + [d.key_item]:
                    if pid in g.owned:
                        continue
                    if cash >= price[pid]:
                        buy(pid)
                    else:
                        break
                if d.key_item in g.owned:
                    key_day, clean_at_key = day, clean
            have_key = d.key_item in g.owned
            for cid, _p, ctier, _a in CATALOG:
                if cid in g.owned or ctier > tier or cid == d.key_item:
                    continue
                if any(p not in g.owned for p in PREREQ.get(cid, [])):
                    continue
                # with the key in hand (or denied): anything affordable. Saving for the key: only small buys
                # (under a tenth of the key's price), so the save-up stays on course.
                if cash >= price[cid] and (have_key or denied or price[cid] <= 0.10 * price[d.key_item]):
                    buy(cid)

            # ---- the day ----
            truck = T_person                     # the truck's working seconds
            people = T_person * hands            # person-seconds
            crew = min(hands, 3)                 # people on the truck
            income = 0.0
            sort_val, treas, crush = bag_values(d, g, pl)
            t_sort = sort_time(d, g, pl)
            tells = max(pl["tells"], (0.0, 0.6, 1.0)[g.vision])
            pushing = have_key and (f_open + f_locked) > 0.0

            # 1. the customers' curbs
            reach = 1.0 if have_key else d.nokey_supply      # Commons: dumpster bags need the Dunker
            put = d.bags * mult * sub * reach
            supply = (put + f_curb) / businesses
            # pushing with a big backlog left: cap the curb round at 55 % of the day; otherwise the curbs come first
            big_backlog = pushing and (f_open + f_locked) > 0.15 * F0
            n_curb = max_collect(d, supply, g, pl, truck * (0.55 if big_backlog else 0.80), crew, have_key)
            t = collect_time(d, n_curb, g, pl, crew, have_key)
            truck -= t
            people -= t * crew
            sweeps = 3 if g.arm >= 2 else 2
            frac, on = pay_fraction(d, sweeps)
            income += n_curb * (d.fee * frac + d.tip * on) * NIGHT_PAY + d.curb * min(d.houses * sub, n_curb / 2.0)
            # what the customers put out and nobody took: the work director throttles put-out when curbs back up,
            # so the carry-over is bounded (about a quarter of a day's put-out)
            f_curb = min(max(0.0, put + f_curb - n_curb * businesses), 0.25 * d.bags * mult)

            # 2./3. backlog and sorting, in the order that pays (pushing for the threshold: backlog first)
            removed = 0.0

            def backlog(truck_budget):
                """Remove filth with the truck for up to truck_budget seconds. Returns seconds used."""
                nonlocal f_open, f_locked, removed
                used = 0.0
                if have_key and f_locked > 0 and truck_budget - used > 0:
                    ut = locked_unit_time(d, g, pl, crew)
                    u = min(f_locked / businesses, (truck_budget - used) / ut)
                    f_locked -= u * businesses
                    removed += u
                    used += u * ut
                if f_open > 0 and truck_budget - used > 0:
                    ut = open_unit_time(d, g, pl, crew)
                    u = min(f_open / businesses, (truck_budget - used) / ut)
                    f_open -= u * businesses
                    removed += u
                    used += u * ut
                return used

            # pushing for the threshold: most of what is left goes on the backlog. Before the key: a third of it
            # where heap work pays about as well as sorting, a tenth where sorting is far richer.
            v_heap = (d.bounty + crush) / open_unit_time(d, g, pl, crew)
            v_sort = (sort_val + treas - crush) / t_sort
            share = 0.75 if pushing else (0.33 if v_heap >= 0.6 * v_sort else 0.10)
            used = backlog(truck * share)
            truck -= used
            people -= used * crew
            bags_in = n_curb + removed
            m_bags = min(bags_in, magnus_bags(d, g))
            stations = 1 if g.table < 2 else 2
            sort_budget = min(people, T_person * stations)
            h_bags = min(bags_in - m_bags, max(0.0, sort_budget) / t_sort)
            people -= h_bags * t_sort
            if hands == 1:
                truck -= h_bags * t_sort          # solo: sorting time is truck time too
            if truck > 1.0 and people > 1.0 and (f_open > 0 or (have_key and f_locked > 0)):
                used = backlog(min(truck, people / crew))
                truck -= used
                people -= used * crew
            bags_all = n_curb + removed
            q = (m_bags + h_bags) / bags_all if bags_all > 0 else 0.0
            fresh_w = n_curb + d.old_loot * removed
            income += (m_bags + h_bags) * sort_val + fresh_w * treas * treasure_share(q, d.valuables, tells)
            income += (bags_all - m_bags - h_bags) * crush
            income += removed * d.bounty

            # 4. won districts, richest first, with whatever is left
            for od in reversed(won):
                spare = min(truck, people / crew)
                if spare <= 40:
                    break
                n_old = max_collect(od, od.bags * mult / businesses, g, pl, spare, crew, True)
                if n_old <= 0:
                    continue
                t = collect_time(od, n_old, g, pl, crew, True)
                truck -= t
                people -= t * crew
                ofrac, oon = pay_fraction(od, sweeps)
                o_sort, o_treas, o_crush = bag_values(od, g, pl)
                o_sorted = min(n_old, max(0.0, min(people, truck if hands == 1 else people)) / sort_time(od, g, pl))
                people -= o_sorted * sort_time(od, g, pl)
                if hands == 1:
                    truck -= o_sorted * sort_time(od, g, pl)
                income += n_old * (od.fee * ofrac + od.tip * oon) * NIGHT_PAY + od.curb * min(od.houses, n_old / 2.0)
                income += o_sorted * o_sort + n_old * o_treas * treasure_share(o_sorted / n_old, od.valuables, tells)
                income += (n_old - o_sorted) * o_crush
            # 5. retainers and work orders
            income += sum(od.retainer * 0.8 for od in won) / businesses     # the retainer is split, as today
            income += pl["orders"] * max(25.0, 0.05 * yesterday_net)
            if day == 1:
                income += 100.0                                 # the Starter collection contract (8 paid pickups)

            # ---- midnight ----
            relief = max(0.40, 1.0 - 0.03 * in_d)              # residents pitch in the longer you are there
            f_open += d.bags * mult * (1.0 - sub) * d.dump * relief
            over = f_open + f_locked + CURB_W * f_curb - F0
            if over > 0:
                f_open = max(0.0, f_open - over)                # it never gets filthier than it started
            clean = max(0.0, 1.0 - (f_open + f_locked + CURB_W * f_curb) / F0)
            best_clean = max(best_clean, clean)
            for i, mark in enumerate((0.25, 0.50, 0.75)):
                if not granted[i] and clean >= mark:
                    granted[i] = True
                    income += d.grant[i] * price[d.key_item]
            cash += income
            income_sum += income
            yesterday_net = income
            if net_first is None:
                net_first = income
            if verbose:
                log.append(f"day {day:3d} {d.key:8s} clean {clean*100:5.1f}%  cust {sub*100:3.0f}%  open {f_open:6.1f} "
                           f"locked {f_locked:6.1f} curb {f_curb:5.1f}  curb bags {n_curb:5.1f}  removed {removed:5.1f}  "
                           f"sorted {m_bags + h_bags:5.1f}  net ${income:8,.0f}  cash ${cash:9,.0f}  "
                           f"key={'Y' if have_key else '-'}")
            if clean >= d.threshold:
                streak += 1
                if thr_day is None:
                    thr_day = day
            else:
                streak = 0
            if streak >= d.hold:
                retries = 1.0 / pl["boss"] - 1.0               # showdown that night; expected nights lost
                cash += d.win_bonus * price[d.key_item] + retries * income
                rows.append(dict(d=d, entered=entered, key=key_day, thr=thr_day, won=day + retries, cash=cash,
                                 net_first=net_first, net_last=income, mean_net=income_sum / in_d,
                                 best_clean=best_clean, clean_at_key=clean_at_key,
                                 days=in_d + retries, won_at=day + lost + retries))
                lost += retries
                won.append(d)
                break
    return Result(rows, rows[-1]["won_at"], log, buys)


# ----------------------------------------------------------------------------------------------------------------
# Reports
# ----------------------------------------------------------------------------------------------------------------
def hours(days):
    return days * DAY / 3600.0


def gear_at_tier(t):
    """Everything in the catalog up to and including tier t."""
    g = Gear()
    for cid, _price, ctier, apply in CATALOG:
        if ctier <= t:
            apply(g)
            g.owned.add(cid)
    return g


def report_tiers():
    print("## 1. Can haul rate alone lock a district?")
    print()
    print("Most backlog bags a day one player can haul from the district to the depot (everything crushed, no curb")
    print("round, no sorting): with all of the previous tier's gear, and with all of this tier's.")
    print()
    print("| District | Previous tier: casual / typical / skilled | This tier: casual / typical / skilled | "
          "Gain (typical) | Skilled on old gear vs casual on new |")
    print("|---|---|---|---|---|")
    for i, d in enumerate(DISTRICTS):
        t = i + 1
        old, new = gear_at_tier(t - 1), gear_at_tier(t)
        vo, vn = [], []
        for p in ("casual", "typical", "skilled"):
            pl = PLAYERS[p]
            T = DAY * pl["work"]
            vo.append(T / open_unit_time(d, old, pl))
            vn.append(T / open_unit_time(d, new, pl))
        if d.key_item == "hoverhog":       # a second vehicle: the truck's own rate does not change
            print(f"| {d.name} | {vo[0]:.0f} / {vo[1]:.0f} / {vo[2]:.0f} | (the key is a second vehicle) | - | - |")
            continue
        print(f"| {d.name} | {vo[0]:.0f} / {vo[1]:.0f} / {vo[2]:.0f} | {vn[0]:.0f} / {vn[1]:.0f} / {vn[2]:.0f} | "
              f"x{vn[1]/vo[1]:.2f} | x{vo[2]/vn[0]:.2f} |")
    print()
    print("Last column at or above x1.00: a skilled player on the old gear out-hauls a casual player on the new gear,")
    print("so no haul-rate threshold can be impossible for the first and reachable for the second.")
    print()


def _cell(r):
    if r is None or r["won"] is None:
        return "not won"
    k = "-" if r["key"] is None else str(r["key"] - r["entered"])
    return f"{r['days']:.1f} (key {k}, threshold {r['thr'] - r['entered']})"


def report_solo(**kw):
    print("## 2. Solo pacing by player type")
    print()
    print("In-game days spent in each district; (key n) = the day in the district the key item is bought;")
    print("(threshold n) = the first midnight at or above the threshold.")
    print()
    print("| District | Key item ($) | Threshold, hold | Casual | Typical | Skilled |")
    print("|---|---|---|---|---|---|")
    res = {p: simulate(p, **kw) for p in PLAYERS}
    kp = kw.get("key_prices") or {}
    for i, d in enumerate(DISTRICTS):
        cells = [_cell(res[p].rows[i]) if i < len(res[p].rows) else "not reached" for p in PLAYERS]
        print(f"| {d.name} | {d.key_item} ({kp.get(d.key_item, PRICE[d.key_item]):,.0f}) | "
              f"{d.threshold*100:.0f}%, {d.hold} | " + " | ".join(cells) + " |")
    print("| **Campaign** | | | " + " | ".join(
        (f"**{res[p].days:.1f} d = {hours(res[p].days):.1f} h**" if res[p].days < 1e9 else "**not finished**")
        for p in PLAYERS) + " |")
    print()
    print("Typical player, net income a day in each district:")
    print()
    print("| District | First day | Mean | Last day | Clean when the key is bought | Key price / mean day | Cash after the contract |")
    print("|---|---|---|---|---|---|---|")
    for r in res["typical"].rows:
        d = r["d"]
        k = kp.get(d.key_item, PRICE[d.key_item])
        cak = "-" if r["clean_at_key"] is None else f"{r['clean_at_key']*100:.0f}%"
        print(f"| {d.name} | ${r['net_first']:,.0f} | ${r['mean_net']:,.0f} | ${r['net_last']:,.0f} | {cak} | "
              f"{k / r['mean_net']:.1f} d | ${r['cash']:,.0f} |")
    print()
    return res


def report_nokey(**kw):
    print("## 3. The lock: 30 days in a district without its key item")
    print()
    print("| District | Threshold | Best clean, casual / typical / skilled | Cap by design (1 - locked share) |")
    print("|---|---|---|---|")
    for i, d in enumerate(DISTRICTS):
        best = []
        for p in PLAYERS:
            r = simulate(p, deny_key=i, **kw)
            best.append(r.rows[i]["best_clean"] if len(r.rows) > i else float("nan"))
        print(f"| {d.name} | {d.threshold*100:.0f}% | {best[0]*100:.0f}% / {best[1]*100:.0f}% / {best[2]*100:.0f}% | "
              f"{(1-d.locked)*100:.0f}% |")
    print()


def _per(r):
    return " / ".join(("-" if x["won"] is None else f"{x['days']:.1f}") for x in r.rows)


def _tot(r, base=None):
    if r.days > 1e9:
        return "not finished", ""
    return f"{r.days:.1f} d = {hours(r.days):.1f} h", (f"x{r.days / base:.2f}" if base else "")


def report_coop(**kw):
    print("## 4. Co-op (typical players)")
    print()
    print("Several businesses in one Town, each with its own truck and its own money:")
    print()
    print("| Businesses | Put-out | Campaign | vs solo | Days per district |")
    print("|---|---|---|---|---|")
    base = simulate("typical", **kw).days
    for n in (1, 2, 3, 4):
        r = simulate("typical", businesses=n, **kw)
        a, b = _tot(r, base)
        print(f"| {n} | x{PUTOUT_MULT[n]} | {a} | {b} | {_per(r)} |")
    print()
    print("One business with hired hands (one truck, put-out x1.0, all income to that business):")
    print()
    print("| People | Campaign | vs solo | Days per district |")
    print("|---|---|---|---|")
    for h in (1, 2, 3, 4):
        r = simulate("typical", hands=h, **kw)
        a, b = _tot(r, base)
        print(f"| {h} | {a} | {b} | {_per(r)} |")
    print()
    floor = sum(d.hold + 1 for d in DISTRICTS)
    print(f"Floor from the holds alone (a day to arrive plus the hold nights, every district): {floor} days = "
          f"{hours(floor):.1f} h.")
    print()


def report_sens(**kw):
    print("## 5. Sensitivity (typical solo, campaign length in days)")
    print()
    print("| Knob | x0.75 | x1.0 | x1.25 |")
    print("|---|---|---|---|")
    b = simulate("typical", **kw).days

    def fmt(x):
        return "not finished" if x > 1e9 else f"{x:.1f}"
    lo, hi = simulate("typical", filth_scale=0.75, **kw).days, simulate("typical", filth_scale=1.25, **kw).days
    print(f"| Starting filth | {fmt(lo)} | {fmt(b)} | {fmt(hi)} |")
    lo, hi = simulate("typical", price_scale=0.75, **kw).days, simulate("typical", price_scale=1.25, **kw).days
    print(f"| All prices | {fmt(lo)} | {fmt(b)} | {fmt(hi)} |")

    def scale(attr, k):
        def f(ds):
            for d in ds:
                setattr(d, attr, getattr(d, attr) * k)
        return f
    for attr, label in (("dump", "Night dumping"), ("treasure", "Treasure value"), ("fee", "Pickup fee"),
                        ("bags", "Bags put out"), ("bounty", "Clean-up bounty")):
        lo = simulate("typical", tweak=scale(attr, 0.75), **kw).days
        hi = simulate("typical", tweak=scale(attr, 1.25), **kw).days
        print(f"| {label} | {fmt(lo)} | {fmt(b)} | {fmt(hi)} |")
    print()


def report_path():
    print("## 7. What a typical solo player buys, and when (day in the district)")
    print()
    r = simulate("typical")
    print("| District | Bought there, in order |")
    print("|---|---|")
    for d in DISTRICTS:
        items = [f"{cid} (day {dd}, ${p:,.0f})" + (" **KEY**" if cid == d.key_item else "")
                 for (dk, dd, cid, p) in r.buys if dk == d.key]
        spent = sum(p for (dk, dd, cid, p) in r.buys if dk == d.key)
        print(f"| {d.name} | " + "; ".join(items) + f". Total ${spent:,.0f} |")
    left = [c[0] for c in CATALOG if c[0] not in {b[2] for b in r.buys}]
    print()
    print("Never bought before the last contract: " + (", ".join(left) if left else "nothing") + ".")
    print()


def report_prices():
    print("## 6. Key price variants (campaign length, casual / typical / skilled, and days in the district concerned)")
    print()
    print("| Variant | Campaign (h) | That district (days) |")
    print("|---|---|---|")
    variants = [("Today's prices", {}, None),
                ("Dumpster Dunker $2,400 -> $6,000", {"dunker1": 6000}, 2),
                ("Octo-Arm $39,000 -> $30,000", {"truck_arm3": 30000}, 4),
                ("Hoverhog $85,000 -> $70,000", {"hoverhog": 70000}, 5),
                ("Hoverhog $85,000 -> $110,000 (with the Bale Mortar)", {"hoverhog": 110000}, 5)]
    for label, kp, idx in variants:
        res = [simulate(p, key_prices=kp) for p in PLAYERS]
        tot = " / ".join("-" if r.days > 1e9 else f"{hours(r.days):.1f}" for r in res)
        if idx is None:
            per = ""
        else:
            per = " / ".join("-" if r.rows[idx]["won"] is None else f"{r.rows[idx]['days']:.1f}" for r in res)
        print(f"| {label} | {tot} | {per} |")
    print()


if __name__ == "__main__":
    which = sys.argv[1] if len(sys.argv) > 1 else "all"
    kw = dict(key_prices=KEY_PRICES) if KEY_PRICES else {}
    if which == "log":
        r = simulate(sys.argv[2] if len(sys.argv) > 2 else "typical", verbose=True,
                     businesses=int(sys.argv[3]) if len(sys.argv) > 3 else 1, **kw)
        print("\n".join(r.log))
        sys.exit(0)
    if which in ("all", "tiers"):
        report_tiers()
    if which in ("all", "solo"):
        report_solo(**kw)
    if which in ("all", "keys", "nokey"):
        report_nokey(**kw)
    if which in ("all", "coop", "crew"):
        report_coop(**kw)
    if which in ("all", "sens"):
        report_sens(**kw)
    if which in ("all", "prices"):
        report_prices()
    if which in ("all", "path"):
        report_path()
