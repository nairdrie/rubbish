# Rubbish: Progression (design + build contract)

> **Compactor update — 2026-10-05:** the current rules supersede the original punitive compaction economy below.
> **COMPACT** processes the chamber, including unopened bags. **BULK COMPACT SHOP** clears eligible trash from only
> that depot's floor/table/drop zone, never truck cargo, held items, saved inventory or displays. Both pay
> **$0.20 / $0.25 / $0.30 / $0.35 per kg** at compactor
> levels 0–3, with **no hazard fines or landfill fees**. Ordinary bales float away and disappear. Recycling's lowest
> base rate remains $0.45/kg, so sorting pays more; forfeited treasure is the main tradeoff. The L3 grate preserves
> treasure at 30% value. Historical simulation and pacing tables were not rerun for this change.

> **Status:** spec, ready to build. Nothing here is built yet. Read `CoreLoop.md` and `LivingWorld.md` first (conventions,
> ownership, setup-script rules, world facts). `Multiplayer.md` is a plan awaiting approval; this spec already follows its §9 rules.
>
> **Made from:** the progression design, the tech-feasibility measurements (live Editor), and a tuned economy simulation that
> ports the real `ItemCatalog.RollBag` and every `Economy` constant. The balance numbers here are the **sim-tuned** ones. Where
> the design and the sim disagreed, the sim won, because it was checked against pacing targets over 8 seeds and 3 player types.
> The production audit, the viral research and `Multiplayer.md` supplied the remaining constraints.
>
> Everything is per in-game day unless stated otherwise. **1 in-game day = 20 real minutes; 1 in-game hour = 50 real seconds.**

---

## 0. The answer in twelve bullets

1. **Four districts on one map, opened by Business Rank plus a permit:** Mildly Pleasant Acres (today's loop), Minivan
   Meadows (rank 4, $600), Snootington Hills (rank 7, $3,500) and Billionaire Bluffs (rank 10, $12,000). A typical player opens
   them at **1.7 h, 3.8 h and 7.0 h**.
2. **Richer districts pay more at the curb and have better bags.** The fee per bag is $4 / $15 / $40 plus a $15 tip / $80 plus
   a $40 tip. Treasure per bag is worth $5 / $27 / $151 / $639. Every richer district has 5–12 exclusive parody valuables.
3. **The starter district's treasure is cut about 4×** (0.08 valuables per bag, $66 average). Today valuables make up about
   90% of income and the whole shop is bought in an hour. After tuning, valuables are **30%** of starter income, rising to 67% in
   the Bluffs.
4. **12 Business Ranks** from *Trash Intern (Unpaid)* to *Supreme Garbage Overlord*, earned with **Rep**. Rep comes from on-time
   bags, clean curbs, correct sorts, kept treasure, clean streets at midnight, Work Orders and shifts worked. Richer districts
   multiply it (×1 / ×1.5 / ×2 / ×3). Each rank also needs a minimum number of **Clean Days**. For solo play this never binds (checked
   against every sim run); it stops a 4-player crew from racing through the districts (`Multiplayer.md` §4.7).
5. **Keeping the city clean pays.** A new clean-streets payout scales with the *worst* district. Per-district **Trust** decides
   how many houses subscribe to you, which sets your supply of bags. Neglected curbs are fined. Playing one lazy sweep a day earns
   **55–76% less** over the first 10–20 days.
6. **About 40 Scrapazon upgrade lines in 7 departments:** truck, garage, Magnus, gear, business, consumables and cosmetics. Each
   level has an exact price, rank gate, prerequisites and effect. Prices run from $15 to $45,000. The capstone **Golden Garbage
   Truck** costs **$140,000** and needs the Key to the City.
7. **Truck automation:** a 3-tier **side-loader arm** (L1 hold X to grab while stopped; L2 automatic while crawling; L3 drives
   the centre line and clears both curbs, plus a litter vacuum). There are also 3 engine tiers capped at the measured safe
   17 m/s, 3 handling tiers, and a hopper that grows to 340 kg.
8. **Garage automation:** an auto-tipper, a 3-tier conveyor, an XL table with a magnet strip, **Compactor Mk II** (auto-bale,
   hazard sneeze, treasure-rescue grate), recycling and hazmat contracts, and a **display line for your finds** (§6.8): a $150
   wobbly plank, the trophy shelf and museum wing, a lit glass case, a pedestal of honor and the Legendary Vault. Kept finds show
   themselves, best find in the best piece, with a placard when you look at them.
9. **Magnus becomes your employee, one career step at a time** (§6.3, reworked 2026-10-01). The first Magnus purchase,
   **"Magnus Gets a Job"** ($500, rank 2), brings a rail branch to your sort station, taped kitchen tongs and Brain Module 1: he
   commutes over and sorts the recyclables and junk on your table from day ~2 (0.75 h). Each of the 8 later steps adds exactly one
   thing: speed, a lobster pincer (heavier items, and he rips bags himself), treasure flags, treasure, hazards, more speed and
   finally the 24 h night shift. Every step raises his **chess strength tier (0–11)**: a blundering ~300 "Elo" at the start,
   **MAGNUS PRIME** at the end. He feeds himself from about 4.7 h, handles everything at about 10.5 h and is MAGNUS PRIME at
   about 10.9 h.
10. **Kevin's Crew** (data only, no driving AI) collects the cheaper districts in the background for a wage and 70% of the fee.
    This is "the truck collects garbage by itself later" at city scale.
11. **Pacing for the typical player** (median of 8 seeds): first purchase at ~5 min, a meaningful purchase every 0.9–1.2 days,
    rank 12 and the Key to the City at **10.3 h**, full tree at **13.2 h**. Skilled players take about 11.9 h and casual players
    about 24 h (re-run with the display line, §6.8).
12. **Built by 4 parallel builders** (§9): A progression core and Scrapazon UI, B truck and gear, C garage and Magnus,
    D districts and loot. First, builder A lands one compile-green **contract step (Step 0)**. After that, every cross-builder call
    goes through Step 0 types only, so no builder waits on another.

### 0.1 Decisions made while merging (design vs. tech vs. sim vs. multiplayer)

| Topic | Chosen | Instead of | Why |
|---|---|---|---|
| All prices, permits and rank thresholds | sim-tuned (20–70% cheaper late game) | design prices ($1M truck, $50k permit) | the design's prices gave an 18.8 h full tree and a 53% treasure share in the starter district |
| District pay | $4 / $15 / $40+$15 / $80+$40 | $4 / $6 / $10 / $20 | the design's treasure buried pickup pay |
| Starter house wealth bands | **unchanged** (0.20–0.35 / 0.45–0.60 / 0.75–0.90) | lowered bands | the sim was tuned on today's bands, and leaving them alone keeps district 1 byte-stable |
| Magnus brain order | L1 recyclables + junk, L2 + treasure, L3 + hazards | L1 recycling, L2 hazard + junk, L3 treasure | treasure sense pays off earlier; hazards need the heavier gripper |
| Magnus sorter layout | **rail branch to your sort station** (tech option A) | Magnus's Desk + pneumatic tubes | measured, cheapest (M), and reuses `DriveTo` + `SortBin.Accept` |
| Magnus purchase order (2026-10-01) | **one career path**: step 1 "Magnus Gets a Job" (brain L1 = rail branch + kitchen tongs + Brain Module 1, $500, rank 2), then one new ability per step | four parallel lines; the $500 rail motor (speed) was the first buy and did nothing, brain L1 needed rail L2 + gripper L1 + conveyor L1 at rank 5 | playtest: "a speed upgrade is useless before he can sort". He now works the table from the first purchase; the conveyor only feeds him |
| Engine tiers | 3 levels, capped at 12.5 / 15 / 17 m/s | 4 levels up to 19 m/s | tyre grip and NetworkTransform limits measured in the live Editor |
| Hopper | 4 levels up to 340 kg | 5 levels up to 400 kg | the sim never needs more than one depot trip per district per day at ≥ 240 kg |
| Roll-off dumpsters | **cut** | 2 levels | they never pay back in the sim |
| Rank gate | Rep **and** Clean Days | Rep only | crew-size-neutral pacing (`Multiplayer.md` rule 11); non-binding solo |
| Arm key | **X** | F | F is `IInteractable.AltInteractKey` and is live while driving |
| Meadows access | a new road east from the depot | extending Maple St east past Oak | Oak Ave's outer lots cover z 55–175, so extending Maple would move district-1 lots |
| New district critters, HOA lawn/honk fines, estate strikes | Phase 2 (Critters owner) | in this build | not simulated, not A–D files |
| Display line (2026-10-01) | 4 new pieces beside the trophy shelf (§6.8); kept finds fill them best-first; a modest **Rep** perk (base Rep while a piece shows a find + Showcase Rep per Rare+ find in the glass case, pedestal ×2 and vault), paid as `trophy_shelf` Rep | an ebae "provenance" bonus for displayed finds | provenance needs per-item flags and an `EbaeMarket` hook and moves the money curve; Rep is the trophy shelf's currency already and barely moves pacing in the sim (§8) |

---

## 1. Pillars and pacing targets

- **One in-game day (20 min) is one shift.** Each shift has 3 Work Orders, a midnight review (clean-streets pay, Trust, fines,
  a Clean Day check, a promotion check) and something worth buying.
- **Throughput is the tension.** Your minutes go to driving sweeps (on-time pay, a clean city), handling bags and sorting
  (treasure). Every upgrade either buys back time or improves what is in the bags.
- **Richer districts mean better bags.** A clean city feeds treasure back through Trust (more subscribers, more rich themes).
- **Automation is the late game.** Kevin covers the cheap districts, Magnus sorts everything, and you chase Butler Drops,
  Legendaries and MAGNUS PRIME.

| Target (typical player) | Goal | Sim result |
|---|---|---|
| First purchase | day 1–2 | about 5 min in (day 0.25) |
| Gap between meaningful purchases | 1–2 days early, 2–3 later | mean 0.94 d before day 10; mean 1.15 d (P90 2.25) after |
| District 2 | day 4–6 | day 5.0 (1.7 h) |
| District 3 | day 10–14 | day 11.8 (3.9 h) |
| Top district | day 20–25 (6–8 h) | day 20.2 (6.8 h) |
| Full tree incl. Golden Truck | 12–15 h | day 39.8 (13.2 h) |
| Starter treasure share of income | ≤ 35% | 31% |
| First Magnus purchase does something visible | the first Magnus buy | "Magnus Gets a Job" at day 2.2 (0.75 h): he sorts at your table |

Numbers re-run 2026-10-01 with the display line (§6.8). Before it (the Magnus career run): gaps 0.87 / 1.20 d, District 2 day 4.8,
District 3 day 12.0, top district day 20.5, full tree day 40.5 (13.5 h), starter share 31%. Before the Magnus career path (§6.3):
gaps 0.86 / 1.07 d, District 2 day 5.0, District 3 day
11.5, top district day 21.0, full tree day 39.8 (13.2 h), starter treasure share 30%, and the first Magnus purchase that did anything
(brain L1, after rail L1 + L2, gripper L1 and conveyor L1) at day 14.2 (4.8 h).

"Meaningful" = price ≥ 25% of that day's net income, or a permit. "Full tree" = every upgrade level in §6 including the Golden
Truck. It excludes consumables and cosmetics, which are optional sinks.

---

## 2. The arc

### 2.1 Day by day (typical player, one representative run: sim seed 1)

Re-run 2026-10-01 with the Magnus career path (§6.3). Magnus purchases in **bold** are his career steps. (Not redrawn for the
display line, §6.8: with it, seed 1 also buys the $150 plank on day 5 and the glass case, pedestal and vault on days 32.75–35.5, and
its valuables draws shift so the Bluffs open on day 20.25 instead of 19; the 8-seed medians are in §2.2.)

| Day | Real time | Rank at end | Districts | Net $/day | What happens |
|---|---|---|---|---|---|
| 1 | 0–20 min | 2 Bag Wrangler | MP | $320 | First buys at ~5 min: `headlamp`, `work_gloves` L1, `air_horn` L1. The first night teaches honking. |
| 2–3 | 20–60 min | 3 Curbside Associate | MP | $384 → $486 | **Career step 1, "Magnus Gets a Job"** ($500): the rail branch appears, he tapes on kitchen tongs, drives to the sort table and says "I AM HELPING". He sorts the recyclables + junk you rip onto the table (8/min). Chess tier 0 → 2. `truck_capacity` L1. |
| 4 | 1.0–1.3 h | **4** Certified Bin Technician | MP | $489 | `recycling_contracts` L1 (Magnus's sorts pay ×1.5 too). The permit becomes buyable. |
| 5–7 | 1.3–2.3 h | 5 | **+ Minivan Meadows** | $804 → $1,398 | `permit_minivan_meadows` ($600). Ribbon ceremony at the gate. `hire_kevin` L1, `truck_turbo` L1, `truck_handling` L1 |
| 8–9 | 2.3–3.0 h | 6 Hopper Whisperer | MP, MM | $1,602–1,806 | **`truck_arm` L1**: first "never leave the cab" stop. `hard_hat`, capacity L2, headlights L1, **step 2: Rail Motor** ($300: commutes twice as fast, ×1.25 work speed; tier 3) |
| 10 | 3.0–3.3 h | **7** Regional Rubbish Manager | MP, MM | $1,952 | `treasure_wand` L1, `work_boots` L1 |
| 11–12 | 3.3–4.0 h | 7 | **+ Snootington Hills** | $1,972 → $3,143 | `permit_snootington_hills` ($3,500) |
| 13–14 | 4.0–4.7 h | 8 Garbage Guru | MP, MM, SH | $5.1k → $6.2k | **step 3: Lobster Pincer** ($1,200): he yoinks bags off the pile and slams them open himself, so he never needs feeding again (tier 4). **Step 4: X-Ray Googly Eyes** ($4,000, tier 5). `hire_kevin` L2, `ebae_power_seller` L1, `truck_turbo` L2, compactor L1, auto-tipper L1, table L1, **`truck_arm` L2** |
| 15–18 | 4.7–6.0 h | 9 Duke of Dumpsters | 3 | $7.0k → $8.8k | capacity L3, horn L2, vaccine L1, compactor L2, handling L2, boots L2, ebae L2, conveyor L1 (now the belt feeds him), recycling L2 |
| 19 | 6.0–6.3 h | **10** Trash Tycoon | **+ Billionaire Bluffs** | $16.5k | `permit_billionaire_bluffs` ($12,000) the day rank 10 lands. The first Butler Drop. `treasure_wand` L2 |
| 20–22 | 6.3–7.3 h | 10 | all 4 | $19.5k → $24.4k | `hire_kevin` L3, capacity L4 (340 kg), turbo L3, compactor L3, table L2, ebae L3, hazmat L1, gloves L3, conveyor L2 |
| 23–25 | 7.3–8.3 h | 11 Baron of Bins | all 4 | $27.3k → $28.3k | **step 5: Treasure Sense** ($12,000: treasure into the KEEP crate, tier 6), **step 6: Appraisal Monocle** ($18,000, tier 7), **step 8: Quad-Claw** ($16,000, tier 8), handling L3, recycling L3, hazmat L2, tipper L2, headlights L2 |
| 27–29 | 8.7–9.7 h | **12** Supreme Garbage Overlord | all 4 | $28.7k → $29.6k | **step 7: Hazmat Certified** ($40,000: he handles everything, tier 9), then **step 9: Grandmaster Module** ($60,000): 24 h shifts and **MAGNUS PRIME** (tier 11) |
| 30–33 | 9.7–11.0 h | 12 | all 4 | $30k | recycling L4, trophy shelf + museum wing, **Key to the City**, `truck_arm` L3 (Octo-Arm), conveyor L3 |
| 34–38 | 11–12.5 h | 12 | all 4 | $30k | Saving up for the **Golden Garbage Truck** ($140,000, about 4.7 days of income; bought at day 37.5). Fill: Trash-dex, beating MAGNUS PRIME, cosmetics. |

### 2.2 Milestones by player type (median of 8 seeds, in-game day ≈ real hours; P10–P90 in brackets)

| Milestone | Casual | Typical | Skilled |
|---|---|---|---|
| First purchase | 0.2 d ≈ 0.1 h | 0.2 d ≈ 0.1 h | 0.2 d ≈ 0.1 h |
| Rank 4 | 5.0 d ≈ 1.7 h | 4.0 d ≈ 1.3 h | 4.0 d ≈ 1.3 h |
| Minivan Meadows | 7.0 [6.8–7.2] ≈ 2.3 h | 5.0 [4.8–5.2] ≈ 1.7 h | 4.2 ≈ 1.4 h |
| **Magnus gets the job** (career step 1: sorts at your table) | 9.8 [8.8–10.8] ≈ 3.2 h | 2.2 [2.0–2.8] ≈ 0.8 h | 2.0 ≈ 0.7 h |
| Magnus rips his own bags (step 3, Lobster Pincer) | 24.2 ≈ 8.1 h | 14.5 [13.5–15.0] ≈ 4.8 h | 11.2 ≈ 3.8 h |
| Rank 7 | 15.0 ≈ 5.0 h | 10.0 ≈ 3.3 h | 8.0 ≈ 2.7 h |
| Snootington Hills | 19.0 ≈ 6.3 h | 11.8 [11.5–12.0] ≈ 3.9 h | 9.8 ≈ 3.2 h |
| Rank 10 | 29.0 ≈ 9.7 h | 19.0 ≈ 6.3 h | 16.0 ≈ 5.3 h |
| Billionaire Bluffs | 33.2 ≈ 11.1 h | 20.2 [19.5–21.2] ≈ 6.8 h | 17.0 ≈ 5.7 h |
| Rank 12 | 48.0 ≈ 16.0 h | 31.0 ≈ 10.3 h | 26.0 ≈ 8.7 h |
| Magnus handles everything (step 7, brain L3) | 65.8 ≈ 21.9 h | 31.0 [29.0–33.5] ≈ 10.3 h | 25.8 ≈ 8.6 h |
| MAGNUS PRIME (step 9, brain L4) | 67.5 ≈ 22.5 h | 32.5 [30.2–35.0] ≈ 10.8 h | 28.8 ≈ 9.6 h |
| Key to the City | 57.0 ≈ 19.0 h | 31.0 ≈ 10.3 h | 26.0 ≈ 8.7 h |
| Full tree incl. Golden Truck | 71.8 [62.8–77.0] ≈ 23.9 h | 39.8 [39.2–44.8] ≈ 13.2 h | 35.8 ≈ 11.9 h |

Re-run 2026-10-01 with the display line (§6.8). The previous run (Magnus career): Meadows 7.0 / 4.8 / 4.2, Snootington 18.2 /
12.0 / 9.5, Bluffs 32.2 / 20.5 / 17.2, rank 12 47 / 30 / 26, brain L3 56.8 / 31.5 / 25.2, MAGNUS PRIME 60.5 / 32.8 / 29.0, Key
to the City 52 / 30 / 26, full tree 65.5 / 40.5 / 34.2 (casual / typical / skilled). Typical and skilled moves are seed noise
(±0.5 day; one seed's $150 plank on day 1 shifted its rank-10 midnight). Casual players lose about 2 h on the late goals: the
sim's casual policy buys the cheap-ish pieces as soon as they "help" (a little Rep) instead of saving for Magnus's big steps.

**Rank ETAs (typical, median day):** r2 1, r3 3, r4 4, r5 6, r6 8, r7 10, r8 13, r9 16, r10 19, r11 24, r12 31.

**Before the Magnus career rework** (the previous run of this sim, 8 seeds): Magnus's first useful purchase (brain L1) came at 29.8 / 14.2 / 11.0 days
(casual / typical / skilled) after three purchases that did nothing on their own; Meadows 7.0 / 5.0 / 4.8, Snootington 19.2 / 11.5 /
10.5, Bluffs 34.0 / 21.0 / 16.8, rank 12 48 / 31 / 26, brain L3 61.5 / 31.5 / 26.2, full tree 70.2 / 39.8 / 33.2. Working from day
~2 lifts early sorting income and Rep a little (rank 12 one day sooner for typical); the dearer late steps ($40k hazmat, $60k
Grandmaster) hold the full tree in place.

**Casual players are 1.6–1.8× slower.** Accepted. Two cheap nudges, both owned by builder A: the Career page shows a "Best next
buy" suggestion, and Head Office sends a letter suggesting Kevin when any district is below 50% clean for 2 midnights in a row and
`hire_kevin` is affordable. Co-op scaling (§9.9) also helps.

---

## 3. Economy rebalance (every constant: old → new)

### 3.1 `Economy` (`Core/RubbishTypes.cs`, lead file, edited in Step 0)

| Constant | Old | New | Why |
|---|---|---|---|
| `RecycleRatePerKg(Metal)` | 1.40 | **2.00** | sorting must beat crushing without leaning on treasure |
| `RecycleRatePerKg(Plastic)` | 0.70 | **1.10** | same |
| `RecycleRatePerKg(Glass)` | 0.35 | **0.55** | same |
| `RecycleRatePerKg(Paper)` | 0.30 | **0.45** | same |
| `MinRecyclePayout` | 0.05 | **0.10** | tiny cans are still worth binning |
| `HazardDisposalPay` | 0.50 | **0.75** | `hazmat_contract` raises it to 1.50 / 4.00 |
| `WrongBinPenalty` | 0.25 | **0.30** | |
| `LazyDumpPerKg` | 0.02 | **0.20** | bulk and chamber share the rate; `compactor_mk2` raises both to 0.25 / 0.30 / 0.35 |
| `CompactorScrapPerKg` | 0.06 | **0.20** | loose scrap never pays less per kg than bulk; both use `UpgradeEffects.CompactPerKgAtLevel` |
| `HazardCompactFine` | 10 | **0** | legacy API retained; hazard effects no longer charge money |
| `LazyCrushHazardBoomChance` | — | **0.25** | bulk's comic hazard effects have no cash penalty; L2 dampens bangs. Hazards are consumed once, never ejected for repeat payouts. |
| `TrickShotUnitOfFee` | — | **0.25** (new) | style pay per unit = 0.25 × that district's fee (§3.5) |
| `TrickShotBand1/2/3` | — | **8 / 12 / 16 m** (new) | distance bands worth 1 / 2 / 3 units |
| `TrickShotStreakStep` / `Cap` | — | **0.25 / 2.5** (new) | streak × (1 + 0.25·(n − 1)), capped at ×2.5 |
| `WorkOrderPayFraction` / `WorkOrderMinPay` / `WorkOrdersPerDay` | — | **0.05 / $25 / 3** (new) | Work Order pay = max($25, 5% of yesterday's net) |
| `KevinFeeShare` / `KevinBurstChance` | — | **0.70 / 0.05** (new) | |
| `TowFee` | — | **$25** (new) | "Hold R" recovery when you don't own `truck_handling` L1 |
| `PickupFeePerBag`, `CurbClearedBonus`, `OnTimeHours`, `LateFloorHours`, `LatePayFloor`, `NeglectedHours` | 4, 2, 4, 12, 0.25, 16 | **unchanged** | these become the Mildly Pleasant row of the district table; richer districts override them through `DistrictDef` |
| `PawnFraction`, `EbaeFeeFraction`, `HospitalBill`, `StartingTruckCapacityKg`, combo | 0.45, 0.08, 100, 160, 2.5 s / +10% / ×2 | **unchanged** | `ebae_power_seller` overrides the fee |

New overloads (Step 0): `Economy.PickupPayFraction(double ageHours, float onTimeHours, float lateFloorHours, float latePayFloor)`
and `Economy.LatenessLabel(double ageHours, float onTimeHours, float lateFloorHours)`. The existing 1-argument versions call them
with the starter constants.

### 3.2 Loot (`Items/ItemCatalog.cs`, builder D)

| Item | Old | New |
|---|---|---|
| `ValuableChance(w)` (= the starter profile) | 0.02 + 0.04·w | **0.005 + 0.008·w** |
| `RarityWeights(w)` Uncommon | Lerp(70, 50) | **Lerp(84, 74)** |
| Rare | Lerp(22, 30) | **Lerp(14, 20)** |
| Epic | Lerp(6.5, 15) | **Lerp(1.7, 5)** |
| Legendary | Lerp(1.5, 5) | **Lerp(0.3, 1.0)** |
| Spring Cleaning valuable chance | ×2.5 + 0.03 | **×2.5 + profile.springFlat** (0.012 in the starter district) |
| `wedding_ring` | Legendary, $1,200–3,200 | **Epic, $400–1,400** (the story item now turns up in the starter suburb) |
| `RollBag(..., LootProfile, trust)` | — | new overload (§5.4). The frozen 4-argument version uses the starter profile. |
| Tags | — | new tag `fragile` on `lava_lamp`, `instapix_camera`, `snow_globe`, `cat_figurine`, `vinyl_record`, `gameboi`, `cursed_doll`, `pocket_watch`, `rollex_watch`, `pigcasso`, `faberge_egg`, `moon_rock`, plus the district items marked F in §5.7. It is consumed by the viral "shatter" slice; no effect in this build. |

**Rookie Luck (new, builder D, no effect on the sim):** while `GameSession.TotalTreasuresFound == 0`, curb bags roll with the
valuable chance ×4. This guarantees an early "wow" now that starter treasure is rarer. It switches off after the first kept find.
It is redundant, and may be removed, once the production audit's seeded onboarding bag (M9) ships.

### 3.3 City, spawner and pay (Trash files, builder D)

| Item | Old | New |
|---|---|---|
| `CityCleanliness.CleanStreetsBonusMax` | $30 | **per district $60 / $250 / $900 / $1,500**. The const stays as the MP value, 60. |
| Clean-streets payout | `Value01 × Max` | **Max_d × clamp01((clean_d − 0.50) / 0.45) × Streak_d × CleanCity**, per unlocked district. **Streak_d** = min(1.5, 1 + 0.1 × consecutive midnights with district d at ≥ 90%). **CleanCity** = 0.4 + 0.6 × clamp01((worst unlocked district − 0.30) / 0.60). |
| `DirtForZero` | 30 | **per district 30 / 26 / 21 / 17** |
| `CityCleanliness.Value01` | city-wide | **bags-per-day-weighted average of unlocked districts** (the HUD keeps working). New: `District01(id)`, `CleanCityFactor`. |
| `BagDirt(age)` | uses the global windows | uses the bag's district windows |
| `CurbTrashSpawner.TargetBagsPerDay` | 35 | **per district 35 / 30 / 24 / 20 × subscribed share**. Subscribed share = 0.25 + 0.75 × Trust. |
| `GlobalCap` | 60 | **per district 60 / 50 / 40 / 34; global 200** |
| Neglect fine (new) | — | **$5 / $40 / $200 / $400** per neglected curb at midnight |
| Compaction landfill surcharge | $0 / $0.50 / $3 / $12 per unopened bag | **removed**; retained district data is no longer charged by the compactor |
| Pickup fee / tip / curb bonus | $4 / — / $2 | per district (§5.1) |

### 3.4 Shop (`Shop/*`)

| Item | Old | New |
|---|---|---|
| `truck_capacity` prices | $150 / $400 / $900 (max 3) | **$150 / $450 / $1,200 / $3,000 (max 4)** |
| Capacity per level | 160 + 40·L (200/240/280) | **200 / 240 / 280 / 340 kg** (table, not a formula) |
| `truck_turbo` | $300 (max 1), torque ×1.35, speed ×1.25 | **$300 / $2,800 / $11,000 (max 3)** with absolute per-level values (§6.1). A saved level 1 gives the same result as today. |
| `air_horn` / `headlamp` | $120 / $80 (max 1) | **$120, $1,200** / **$80, $800** (max 2) |
| `conveyor_belt`, `magnus_brain` | teasers | real upgrades (§6.2, §6.3) |
| `ShopEffects.AirHornScareRadius` | 25 | L1 25 m, **L2 40 m** (base stays 12 m) |
| Everything else in §6 | — | new |

### 3.5 New mechanics this spec adopts

All were in the sim. Each needs Nick's OK (§10).

- **Trick-shot style pay** (built by the "Yeet or Baby It" slice, not by A–D): units = distance band (8–12 m = 1, 12–16 m = 2,
  16 m+ = 3) + 1 per tag (BANK, OFF THE <thing>, SKYHOOK). Pay = units × 0.25 × the **district fee** × streak. In the starter
  district that is exactly the slice's +$1 / +$2 / +$3. It is 5–7% of income. Scripted tosses (E-toss, the arm, Kevin) never count.
- **Landfill surcharge** was retired from compaction on 2026-10-05; crushing rich bags unopened now only sacrifices their contents' sorting/treasure value.
- **Bulk compaction can play a comic hazard effect:** a 25% chance per hazard in the load, with no cash penalty.
- **New clean-streets formula** with a streak and a Clean City factor (above).
- **Neglect fines** (above).
- **Shift Rep:** +150 Rep × your top district's Rep multiplier for every shift worked.
- **Work Orders:** pay = max($25, 5% of yesterday's net) + 40 Rep × your top district's multiplier.

---

## 4. Business Rank and Rep

### 4.1 Rep sources

Every event is multiplied by the **Rep multiplier of the district it happened in** (×1 / ×1.5 / ×2 / ×3), unless marked "top".
"Top" means the multiplier of your highest unlocked district. Rep is computed in one place (builder A, from the facts the other
builders raise; §9.3). Nobody else hard-codes Rep values.

| Rep id | Rep | When |
|---|---|---|
| `bag_ontime` | +3 | a curb bag enters the hopper within its district's on-time window |
| `bag_late` | +1 | a late bag (any age) |
| `curb_clean` | +2 | the house's last curb bag was collected |
| `item_sorted` | +0.5 | an item went into its correct bin (recyclables, hazard barrel, junk into the compactor, valuables into the keep crate). Magnus's sorts count ("your employee, your credit"). A CRUSH ALL gives nothing. |
| `contamination` | −2 | wrong bin (Magnus's misses too) |
| `treasure_uncommon` / `_rare` / `_epic` / `_legendary` | +5 / +12 / +25 / +50 | a valuable goes into the KEEP crate (by you or Magnus) |
| `clean_streets` | +120 × clean² | per unlocked district at midnight (clean = 0..1) |
| `neglected_curb` | −3 | per neglected curb at midnight |
| `shift` | +150 (top) | at midnight, if at least 5 bags went into your truck that day |
| `work_order` | +40 (top) | per completed Work Order |
| `chess_win` | +25 × max(1, chess tier) | beating Magnus; once per in-game day |
| `trophy_shelf` | +40 / +160, + the display line | at midnight, with `trophy_shelf` L1 / L2; plus (§6.8) the glass case +10, pedestal +25 and vault +40 while each shows a find, and Showcase Rep for each find in them: Rare +2, Epic +4, Legendary +8 (×2 on the pedestal). Paid in one entry through `UpgradeEffects.TrophyRepPerDay`. |
| Kevin's bags | half of `bag_ontime` / `bag_late` | Kevin's Crew (§6.5) |

Rep can dip (penalties), but **rank never goes down.** The highest rank reached is stored.

### 4.2 Clean Days

A midnight counts as a **Clean Day** when both are true:
- the **highest-tier unlocked district** is at least **70% clean**;
- at least **5 bags** went into your truck since the previous midnight.

Both are percentages or flat counts, so crew size doesn't change them. In all 24 sim runs (3 player types × 8 seeds) the Clean Day
requirement below **never delayed a promotion**. A 4-player crew earning Rep ~2× faster is held to about one rank per in-game day:
Meadows no earlier than day 3, Snootington day 7, Bluffs day 13, rank 12 day 21.

(A stricter version, "and every unlocked district at least 40%", would delay greedy casual players by up to 3 days. It is kept as a
tuning knob, off.)

### 4.3 The 12 ranks

A promotion needs **Rep ≥ threshold AND Clean Days ≥ minimum**. It is checked on every Rep change and at every midnight.

| # | id | Title | Rep | Clean Days | Typical ETA | Unlocks on Scrapazon (level: rank) |
|---|---|---|---|---|---|---|
| 1 | `rank_trash_intern` | Trash Intern (Unpaid) | 0 | 0 | start | capacity L1, air horn L1, headlamp L1, gloves L1, wobbly plank shelf, garbage juice, rabies shot, repellent |
| 2 | `rank_bag_wrangler` | Bag Wrangler | 450 | 0 | ~20 min | capacity L2, turbo L1, handling L1, headlights L1, **Magnus's job (brain L1, career step 1)**, boots L1, primer paint |
| 3 | `rank_curbside_associate` | Curbside Associate | 950 | 0 | 1.0 h | capacity L3, **arm L1**, auto-tipper L1, table L1, compactor L1, recycling L1, Magnus rail motor (step 2), hard hat, lucky gnome, Trash FM radio |
| 4 | `rank_certified_bin_technician` | Certified Bin Technician | 1,800 | 2 | 1.3 h | **permit Minivan Meadows**, turbo L2, handling L2, conveyor L1, hazmat L1, trophy shelf L1, wand L1, vaccine L1, ebae L1, **Kevin L1**, Magnus lobster pincer (gripper L1, step 3), musical horn pack |
| 5 | `rank_senior_sack_specialist` | Senior Sack Specialist | 3,250 | 3 | 2.0 h | capacity L4, compactor L2, recycling L2, Magnus googly eyes (vision L1, step 4), gloves L2, flames paint, disco ball |
| 6 | `rank_hopper_whisperer` | Hopper Whisperer | 4,900 | 4 | 2.7 h | **arm L2**, horn L2, headlights L2, tipper L2, conveyor L2, table L2, boots L2, headlamp L2, lit glass case |
| 7 | `rank_regional_rubbish_manager` | Regional Rubbish Manager | 6,650 | 6 | 3.3 h | **permit Snootington Hills**, turbo L3, brain L2 (step 5), hazmat L2, wand L2, vaccine L2, ebae L2, Kevin L2, raccoon-camo paint |
| 8 | `rank_garbage_guru` | Garbage Guru | 9,600 | 8 | 4.3 h | handling L3, recycling L3, museum wing, vision L2 (step 6), gloves L3 |
| 9 | `rank_duke_of_dumpsters` | Duke of Dumpsters | 13,100 | 9 | 5.3 h | conveyor L3, compactor L3, **brain L3** (step 7), pedestal of honor, golden gnome, chrome paint |
| 10 | `rank_trash_tycoon` | Trash Tycoon | 17,400 | 12 | 6.3 h | **permit Billionaire Bluffs**, **arm L3**, Magnus quad-claw (gripper L2, step 8), ebae L3, Kevin L3 |
| 11 | `rank_baron_of_bins` | Baron of Bins | 27,550 | 15 | 8.0 h | recycling L4, **brain L4** (step 9, MAGNUS PRIME), **the Legendary Vault** |
| 12 | `rank_supreme_garbage_overlord` | Supreme Garbage Overlord | 43,900 | 20 | 10.0 h | Key to the City quest, **Golden Garbage Truck** |

**Sensitivity:** the Rep-per-rank knob is the sharpest lever in the sim (±25% moves the top district from day 16.5 to 23.8). Tune
this table before touching anything else.

### 4.4 Promotion (v1, builder A)

The moment you qualify, even mid-route:
- a rubber-stamp card slams onto the HUD: **"PROMOTED: HOPPER WHISPERER"**, with a confetti burst (`Fx.Burst` ×4 colours), the
  `legendary` Sfx and `Juice.Shake(0.25, 0.3)`;
- a toast: "New on Scrapazon: 9 items";
- a deadpan **Head Office** line, one per rank:
  - r2: "Congratulations. You may now touch the bags with both hands."
  - r3: "Your business cards are on order. They say 'Curbside Associat'. We ran out of E."
  - r4: "Please stop telling residents you are a doctor."
  - r5: "Your salary is unchanged. Please stop feeding the raccoons."
  - r6: "The truck has asked HR about you. HR is a raccoon."
  - r7: "You now manage a region. The region is mostly hedges."
  - r8: "Staff report you meditating in the compactor. Please don't."
  - r9: "Your title is legally binding in zero countries."
  - r10: "The billionaires have asked for you by name. They got it wrong."
  - r11: "Magnus has requested a raise on your behalf. Denied."
  - r12: "There are no more promotions. We checked. We're a little scared of you."

Phase 2 (C or Feel, optional): a garage ceremony with a spotlight on an "EMPLOYEE OF THE MONTH" frame, a Magnus kazoo `Celebrate`,
and a certificate dropping on the table. Rank hats (Backwards Cap … Golden Crown) become locker cosmetics once Chuck's headwear swap
exists (`Character.md` §14); this build does not change Chuck.

### 4.5 Work Orders (3 per shift, builder A)

They are rolled at midnight, deterministically from (Day, company seed), and shown on the Career page and in a toast at dawn.
Reward: **max($25, 5% of yesterday's net) + 40 Rep × top multiplier** each. Progress is persisted with `GameSession` counters.
The midnight-judged orders (`wo_clean_district`, `wo_no_crush`) are judged on the **stored set of the day that just ended**,
and the new set rolls afterwards with the daily stats (`WorkOrders.Today` keeps showing the ended day until that review runs).
*Fixed 2026-10-01:* the review used to read `WorkOrders.Today`, which rolled the new day's set first (the clock had already
moved on) and judged that: a brand-new "No CRUSH ALL today" paid out at 00:00 and the ended day's orders were never judged.
"Yesterday's net" counts every positive `CashChanged` (so not-yet-migrated `AddCash` sites show up), so a test that grants
cash with `GameSession.AddCash` inflates the next day's Work Order pay (the verifier's ~$152k of grants → a $7.6k order).

| id | Text | Source fact (§9.3) | Only when |
|---|---|---|---|
| `wo_collect_ontime` | "Collect {N} bags on time" (N = round(0.4 × today's expected bags)) | `BagCollected.onTime` | always |
| `wo_clean_district` | "Keep {district} ≥ 80% clean at midnight" | `Midnight.clean01` | always |
| `wo_find_rare` | "Find a Rare or better" | `TreasureKept` | always |
| `wo_clean_sort` | "Sort 40 items in a row without contamination" | `ItemSorted` | always |
| `wo_curbs` | "Leave {N} curbs spotless" (N = 8 + 2 × districts) | `CurbCleared` | always |
| `wo_ebae_fair` | "Sell something on ebae at Fair or better" | `EbaeMarket.ItemSold` | always |
| `wo_no_crush` | "No CRUSH ALL today" | `LoadCrushed` | rank ≥ 3 |
| `wo_butler` | "Clear a Butler Drop within 2 h" | `BagCollected` (Bluffs, age ≤ 2 h, 5 in one drop) | Bluffs unlocked |
| `wo_magnus_chess` | "Beat Magnus, or survive 20 moves" | `ChessGameEnded` | chess tier ≥ 1 |
| `wo_trickshot` | "Land a 12 m throw in the hopper" | TrickShot slice | once that slice exists |

### 4.6 Neglect pressure

The sim shows a greedy player dropping the starter district as soon as Meadows opens. The counter-pressure, all simulated:
- neglect fines and −3 Rep per neglected curb;
- Trust falls 10 points a night when a district is below 65% clean, so fewer houses subscribe;
- the Clean City factor shrinks **every** district's clean-streets payout when any one district is dirty;
- Kevin L1 arrives at the same rank as the Meadows permit.

A district below 50% at midnight gets a flavour-only Head Office letter ("Mayor Bandit's office called. Twice."). Two nights in a
row triggers the Kevin nudge (§2.2). No extra penalty beyond the simulated ones.

---

## 5. Districts

### 5.1 Overview and rules (`DistrictDef` rows, Step 0)

| Field | `mildly_pleasant` | `minivan_meadows` | `snootington_hills` | `billionaire_bluffs` |
|---|---|---|---|---|
| Name | Mildly Pleasant Acres | Minivan Meadows ("An HOA Community") | Snootington Hills | Billionaire Bluffs |
| Tier / stopId prefix | 1 / (none) | 2 / `mm-` | 3 / `sh-` | 4 / `bb-` |
| Houses | 37 (today's) | 22 | 14 estates | 8 mega-estates |
| Unlock | start | rank 4 + `permit_minivan_meadows` $600 | rank 7 + `permit_snootington_hills` $3,500 | rank 10 + `permit_billionaire_bluffs` $12,000 |
| Bags/day at 100% Trust | 35 | 30 | 24 | 20 |
| Curb capacity per house (avg) / district cap | 4.5 / 60 | 5 / 50 | 6 / 40 | 6 / 34 |
| Fee per bag (on time) | $4 | $15 | $40 | $80 |
| Tip per on-time bag | — | — | $15 | $40 "butler tip" |
| Curb-clean bonus | $2 | $8 | $30 | $80 |
| On time / pay floor at / floor pay | 4 h / 12 h / 25% | 3.5 h / 10 h / 20% | 3 h / 8 h / 10% | 2 h / 6 h / **0%** |
| Neglected at | 16 h | 14 h | 12 h | 8 h |
| Clean-streets max / DirtForZero | $60 / 30 | $250 / 26 | $900 / 21 | $1,500 / 17 |
| Neglect fine per curb | $5 | $40 ("HOA letter") | $200 ("HOA tribunal") | $400 ("estate complaint") |
| Compaction landfill surcharge | $0 | $0 | $0 | $0 |
| Rep multiplier | ×1 | ×1.5 | ×2 | ×3 |
| Trust at unlock | 60% | 40% | 40% | 40% |
| Put-out pattern | trickle (today's hour weights) | trickle | trickle | **Butler Drops only** at 07:00 and 17:00 (±10 min): each subscribed estate puts out 1–2 bags (P(2) = 0.25) |
| Bag mass / items per bag | 5.7 kg / 5–10 | 6.7 kg / 6–11 | 8.5 kg / 7–12 | 9.6 kg / 8–13 |
| Critter pressure (data for Critters) | ×1.0 | ×0.9 | ×0.85 | ×0.8 |
| Loop / access from depot (sim geometry) | 540 m / 74 m | 720 m / 260 m | 900 m / 480 m | 1000 m / 700 m |

Pay for a curb bag = fee × `PickupPayFraction(age, district windows)` + (on time ? tip : 0). The curb bonus is paid when a house's
last bag is collected.

### 5.2 Trust and subscribers

- **Trust** (0–100%) is stored per district. The starter district starts at 60%; a district starts at 40% when its permit is bought.
- **At midnight:** +15 points if the district is at least 85% clean, +5 if at least 65%, otherwise −10. Clamped to 0–100.
- **Subscribers** = round(houses × (0.25 + 0.75 × Trust)). Subscribed houses are the first N houses in the district ordered by
  `StableHash(stopId)`, so the set grows and shrinks in a stable order. Only subscribed houses put out trash for you. Unsubscribed
  houses (and every house in a locked district) have a gold-star "GoldStar Premium Waste" sticker on their bin (the rival company,
  lore only).
- **Rich bag themes** scale with Trust (§5.5).
- A new district therefore ramps up gently: Meadows opens with 12 of 22 houses subscribed and reaches all 22 after 4 good nights.

### 5.3 Map: how the districts connect to today's Neighborhood

All districts live in the **one Neighborhood scene**. Separate scenes break curb trash, because `RegisterSceneHouses` deletes
records for houses that aren't loaded, and every crossing would be a Single load that moves every co-op player at once.

**Today:** PlayBounds x −60..220, z −40..230. Streets: Elm Ave x=0, Oak Ave x=160, Maple St z=60, Birch St z=170, Depot Rd
x=0 (z 12..55). Depot pad x −18..18, z −24..12. Park x 35..125, z 98..132. Oak Ave's outer (east) lots cover z 55..175 and Maple
St's outer (south) lots cover z 18..50.

**New PlayBounds:** x −60..440, z −40..640. District 1 is built first and stays **byte-stable**: same lots, stopIds, wealth and
positions. New districts use their own RNG (`seed ^ StableHash(districtId)`) and unique street keys. Only the nature scatter
changes.

| District | Connection | Gate | Layout (flat, y = 0, in v1) | Bounds for `Districts.AtPosition` |
|---|---|---|---|---|
| **Minivan Meadows** (east) | **Minivan Mile**: a new two-lane road leaving the east side of the depot pad at z ≈ −6 and running east to x ≈ 225, south of Maple's outer lots. It is aligned so it can later become `Multiplayer.md`'s Depot Row service road, whose east end is its "gate ▸ District 2". | **Meadows Gate** at x ≈ 205: a cheesy arch, "MINIVAN MEADOWS: Pop. 64 minivans" | loop x 240..400, z −10..150: **Carpool Ln** (z −6, continuing the Mile), **Soccer Mom Way** (x 400), **Juice Box Blvd** (z 150), **Trampoline Ct** (x 240), plus 2 cul-de-sac spurs. Lots about 26 m. | x 200..440, z −40..230 |
| **Snootington Hills** (north) | **Old Money Rd**: Elm Ave (x=0) continues north past Birch St. The corridor x −5..5 is free of lots; skip verge furniture in the road footprint. | **Snootington Gate** at z ≈ 225: wrought iron between stone pillars (`SM_pillar_01/02`, dark-tinted `SM_fence_02` / `SM_garden_door`) | loop z 240..420, x −40..180: **Hedgerow Crescent**, **Pinky-Up Place**, **Monocle Mews**. Lots about 40 m. Bags sit at the curb outside hedge gates. | x −60..220, z 230..460 |
| **Billionaire Bluffs** (far north) | **Tax Haven Heights Rd** continues north from the top of Old Money Rd | **boom barrier + guard booth** at (80, 470). A bored guard says "Name? …Garbage? …Fine." | plateau z 480..620, x 0..300: 8 estates, lots 70–90 m | x 0..340, z 460..640 |

- **Elevation is follow-up polish, not v1.** Snootington terraces (+3 m) and the Bluffs plateau (+11 m) with ramps no steeper than
  5% come after the flat version passes a playtest. The measured truck (1,500 kg, WheelColliders) copes with ≤ 6% grades.
- **Performance budget:** each district like today's costs about +1.8k GameObjects, +1.24k renderers and +0.97k colliders. The
  new districts have fewer, bigger lots, so all four together should stay under about 5.5k GameObjects. Street lamps still pool 8
  real-time lights. Keep static batching; enable the GPU Resident Drawer only if profiling asks for it.
- **Gates (`World/DistrictGate.cs`, builder D).** A barrier collider on layer Walls (7) spans the road, plus an `InteractionZone`
  (Either, about 10 × 4 × 8 m). Locked prompt: "Minivan Meadows: needs Rank 4 (Certified Bin Technician) + Collection Permit
  ($600)". When the rank is met, [E] at the gate offers "Buy permit $600", which goes through `ScrapazonStore.TryBuy`. The gate
  opens when `GameSession.GetUpgradeLevel(permitId) > 0`, instantly on scene load. **Vehicles only:** a turnstile lets people walk
  in and gawk; locked houses have no trash, because nothing spawns there. The first drive-through after unlocking snaps a giant red
  ribbon: confetti, a welcome card ("WELCOME TO SNOOTINGTON HILLS: Please Wipe Your Tires"), and `ProgressionEvents.DistrictEntered`.
  Per-district perimeter walls leave the gate as the only road opening.
- **Look** (built from the packs on disk). The only house prefabs are `SM_house_suburb_001..007`, so the look comes from scale,
  materials and props.
  - **Meadows:** houses 003–007 at 1.1–1.2×, pastel tints; white picket fences, trampolines (disc + net), basketball hoops,
    above-ground pools (`SM_swimming_pool`), lemonade stands (`SM_lemonade_shop`), "LIVE LAUGH LOAD" yard signs, and two
    stretched beige or teal "minivans" per driveway (`SM_car_town_*` ×1.15 on z).
  - **Snootington:** houses 006/007 at 1.3×, cream and white, pillared porches; 3 m boxy hedges, topiary poodles (sphere clusters),
    fountains, tennis courts, hot tubs (`SM_whirlpool`), gravel turnarounds.
  - **Bluffs:** two-wing mansions (006 + 007 side by side at 1.6×, white and gold), helipads, a giant gold statue of the owner,
    moats, a yacht parked on the lawn, a hedge maze, infinity pools, a small rocket pad, and a butler NPC (static prop) at each curb
    holding a silver platter.
- **House wealth by size**, as builder bands (the sim's bag-weighted mix):

| District | Small | Medium | Big |
|---|---|---|---|
| Mildly Pleasant (unchanged) | 24%: 0.20–0.35 | 46%: 0.45–0.60 | 30%: 0.75–0.90 |
| Minivan Meadows | 30%: 0.30–0.45 | 45%: 0.45–0.65 | 25%: 0.65–0.80 |
| Snootington Hills | 30%: 0.40–0.60 | 40%: 0.60–0.80 | 30%: 0.80–0.90 |
| Billionaire Bluffs | — | 50%: 0.60–0.85 | 50%: 0.85–1.00 |

### 5.4 Loot profiles (`LootProfile` rows, Step 0; rolled by D's `RollBag` overload)

| Profile | p = p0 + p1·w | Rarity U/R/E/L at w=0 → w=1 | Items per bag | Category weights J/P/Pl/M/G/H | Tier pool boost (item tier: ×) | Spring flat |
|---|---|---|---|---|---|---|
| `mildly_pleasant` | 0.0050 + 0.0080·w | 84/14/1.7/0.3 → 74/20/5/1 | 5–10 | 40/18/15/11/10/4 (today's) | 1: ×1 | 0.012 |
| `minivan_meadows` | 0.0060 + 0.0070·w | 68/25/6/1 → 58/30/10/2 | 6–11 | 34/22/16/12/11/5 | 1: ×1, 2: ×5 | 0.020 |
| `snootington_hills` | 0.0068 + 0.0068·w | 52/32/13/3 → 42/35/18/5 | 7–12 | 28/22/12/14/18/6 | 1: ×0.6, 2: ×2, 3: ×8 | 0.025 |
| `billionaire_bluffs` | 0.0066 + 0.0049·w | 55/30/12/3 → 47/33/15/5 | 8–13 | 24/20/10/16/20/10 | 1: ×0.25, 2: ×0.8, 3: ×3, 4: ×12 | 0.030 |

**Rolling rules** (exactly what the sim did):
1. The valuable pool is every valuable with item tier ≤ the profile tier, each weighted by `weight × tierBoost[tier]`.
   Existing items are tier 1.
2. The non-valuable pool is every item with tier ≤ the profile tier. District items (tier ≥ 2) are weighted ×3.
3. The profile's category weights replace the plain ("None") theme's weights and the rich themes' weights. The 4 existing themes
   keep their own category weights.
4. The 4 existing themes roll exactly as today, except that Spring Cleaning's flat bonus is `profile.springFlat`.
5. `wedding_ring` is Epic ($400–1,400) in every profile.
6. The Min and Max bag kg (4 / 18), the bulk filler and the shuffle are unchanged.

**Resulting bags** (Monte Carlo of the ported roll at 100% Trust; builder D's acceptance test must land within ±10%):

| District | Valuables per bag | Mean valuable | Treasure EV per bag | Rarity mix U/R/E/L | Bag mass |
|---|---|---|---|---|---|
| Mildly Pleasant | 0.082 | $66 | $5 | 79/16/5/0.4% | 5.7 kg |
| Minivan Meadows | 0.138 | $196 | $27 | 62/28/8/1% | 6.7 kg |
| Snootington Hills | 0.190 | $798 | $151 | 45/34/16/5% | 8.5 kg |
| Billionaire Bluffs | 0.254 | $2,513 | $639 | 37/37/21/5% | 9.6 kg |
| *(today's code)* | *0.361* | *$195* | *$70* | *57/29/11/3%* | *5.8 kg* |

### 5.5 Rich bag themes (tier 2+, extend the private `Theme` enum)

Chance per bag = table chance × (0.5 + 1.5 × Trust) / 2. At 100% Trust that is the table value; at 40% Trust it is 55% of it.
Rich themes are rolled after the 4 base themes miss.

| Theme id | Label (`BagTheme`) | Min tier | Chance at 100% Trust | Valuable chance × | Extra picks | Guarantee |
|---|---|---|---|---|---|---|
| `divorce_bag` | "Divorce Bag" | 2 | 4% | ×2.5 | 0 | — |
| `moving_day` | "Moving Day" | 2 | 5% | ×1.8 | +4 | — |
| `influencer_haul` | "Influencer Unboxing Haul" | 2 | 3% | ×1.0 | 0 | at least one Uncommon+ |
| `estate_cleanout` | "Estate Clean-Out!" | 3 | 6% | ×3.0 | 0 | — ("old" tag boost ×5) |
| `yacht_party` | "Yacht Party" | 4 | 5% | ×1.0 | 0 | at least one Rare+ |
| `butler_mixup` | "Butler Mix-Up!" | 4 | 1% | ×1.0 | 0 | at least one Epic+ |

A guarantee swaps the first non-valuable for a valuable of that minimum rarity (rarity rolled from the profile's weights at or
above that rarity). Phase 2 story hook: the Butler Mix-Up item is asked back at the depot (return it for a big tip and Trust, or sell
it and get an angry letter).

### 5.6 How else treasure gets more likely (all simulated)

1. **District tier:** the base chance, rarity weights and exclusive pools (§5.4).
2. **House wealth** within the district.
3. **Trust:** more subscribed houses and more rich themes.
4. **Detection:** `magnus_vision`, `treasure_wand` and the UV `headlamp` L2 let you hand-sort the flagged bags and crush the rest
   (§6). They raise how much treasure you *capture*, not how much exists.
5. **Consumables:** `lucky_gnome`, `golden_gnome` (§6.6, not simulated; priced at break-even).
6. **Bag tells** (viral slice: jingle, clink, gold twist-ties on w > 0.7 houses). The sim assumes 30/60/80% of treasure bags are
   recognised by casual/typical/skilled players.

### 5.7 District-exclusive valuables (31; builder D)

Columns: rarity, mass kg, value $, tier, pick weight, `visual` key for `ItemVisuals` (unknown keys fall back to "blob", so art can
land later), F = `fragile`.

| id | Name | R | kg | $ | T | Wt | visual | F | Flavor |
|---|---|---|---|---|---|---|---|---|---|
| `air_fryer_xxl` | Air Fryer XXL (Used Once) | U | 3.5–5.0 | 40–110 | 2 | 1.2 | airfryer | F | Fried one (1) nugget. Achieved enlightenment. Retired. |
| `yeeti_tumbler` | Yeet-i Mega Tumbler | U | 0.4–0.7 | 25–60 | 2 | 1.2 | tumbler | | Keeps coffee hot for 3 days. Owner's interest lasted 3 hours. |
| `essential_oil_crate` | Crate of Essential Oils (MLM) | U | 1.5–3.0 | 20–90 | 2 | 1.0 | oilcrate | | Cures nothing. Smells like everything. Aunt Denise is Diamond Rank now. |
| `fantasy_trophy` | Fantasy Football League Trophy | U | 1.0–2.0 | 30–120 | 2 | 1.0 | trophy_big | | Won by Brad, 2019. Brad has mentioned it 4,000 times. Brad threw it out after the divorce. |
| `pedaltron_seat` | Pedaltron Smart-Bike Seat | R | 0.8–1.2 | 150–400 | 2 | 1.0 | bikeseat | | The bike is still in the living room. It is a coat rack now. |
| `crashed_drone` | Crashed Camera Drone | R | 0.9–1.4 | 180–520 | 2 | 1.0 | drone | F | Last recorded footage: a tree, getting closer. |
| `occulus_wift` | Occulus Wift VR Headset | R | 0.5–0.7 | 200–480 | 2 | 1.0 | vrheadset | F | Smells like forehead. Last played: 'Beat Sabre', Expert, 4 hours straight. |
| `air_jordumb` | Air Jordumb Retro Sneakers | E | 0.8–1.2 | 600–2,200 | 2 | 1.0 | sneakers | | Deadstock! Worn once, to the wrong wedding. |
| `bon_jovial_guitar` | Guitar Signed by 'Bon Jovial' | E | 3.0–4.5 | 800–2,600 | 2 | 0.8 | guitar | F | Signed in Sharpie. Or ketchup. The autograph expert fainted either way. |
| `minivan_title` | Pink Slip to a Minivan | L | 0.01–0.02 | 2,500–6,000 | 2 | 0.8 | deed | | The family 'wanted something sportier'. They bought a slightly bigger minivan. |
| `monogram_robe` | Monogrammed Silk Robe | U | 0.8–1.2 | 80–220 | 3 | 1.0 | robe | | Embroidered 'H.W.T. III'. The first two are also in the trash. |
| `caviar_tin` | Tin of Beluga-ish Caviar | R | 0.2–0.4 | 300–800 | 3 | 1.0 | caviar | F | Tiny fish eggs. Tiny price? Absolutely not. |
| `sterling_tea_set` | Sterling Silver Tea Set | R | 3.0–5.0 | 400–1,100 | 3 | 1.0 | teaset | F | Pinkies up. One handle snapped because a pinky went TOO up. |
| `polo_cup` | Polo Championship Cup | R | 2.0–3.5 | 350–950 | 3 | 0.9 | trophy_cup | | Awarded to a horse. The horse was not consulted. |
| `louis_vuitto` | 'Louis Vuitto' Handbag | E | 0.8–1.3 | 1,200–4,200 | 3 | 1.0 | handbag | | Designer! Real? Unclear. The zipper says 'Luis'. |
| `chateau_fance` | Chateau Le Fance 1961 | E | 1.3–1.6 | 1,000–3,800 | 3 | 1.0 | wine_fancy | F | Aged to perfection. Then aged a bit more. Then forgotten behind the good cheese. |
| `poodle_portrait` | Oil Portrait of a Pampered Poodle | E | 4.0–7.0 | 1,400–4,800 | 3 | 0.8 | painting_poodle | F | Commissioned. Framed in gold. The poodle hated it. |
| `gold_putter` | Gold-Plated Putter | E | 0.9–1.3 | 1,100–3,600 | 3 | 0.9 | putter | | Scored one hole-in-one, then retired so it could never miss again. |
| `dented_chandelier` | Crystal Chandelier (Slightly Dented) | L | 9.0–14.0 | 5,000–14,000 | 3 | 0.7 | chandelier | F | Fell during a dinner party. Nobody said anything. Very polite people. |
| `heirloom_tiara` | Great-Grandmama's Tiara | L | 0.3–0.5 | 6,000–16,000 | 3 | 0.8 | tiara | | Worn to 3 coronations and 1 very awkward Applebee's. |
| `emerald_monocle` | Emerald Monocle | E | 0.05–0.10 | 3,000–9,000 | 4 | 1.0 | monocle | F | Popped out in shock when the stock dipped 0.2%. |
| `diamond_dog_collar` | Diamond Dog Collar | E | 0.2–0.4 | 4,000–12,000 | 4 | 1.0 | dogcollar | | The dog upgraded to platinum. This was the 'casual' collar. |
| `platinum_tamagochu` | Platinum Tamagochu (1 of 3) | E | 0.08–0.12 | 3,500–10,000 | 4 | 0.9 | tamagochu_plat | F | Still hungry. Hungrier. Richer. |
| `celebrity_toupee` | A-List Toupee (Signed Inside) | E | 0.1–0.2 | 2,500–11,000 | 4 | 0.9 | toupee | | You know whose it is. You can't say whose it is. Legal says no. |
| `nft_hard_drive` | Hard Drive Full of NFTs | L | 0.3–0.5 | 1–60,000 | 4 | 1.0 | harddrive | F | 4,000 cartoon apes. Worth millions! Worth nothing! Depends on the minute. |
| `gold_toilet_seat` | Solid Gold Toilet Seat | L | 16–22 | 15,000–40,000 | 4 | 0.8 | toiletseat_gold | | Cold. So, so cold. Heavier than your life choices. |
| `yacht_keys` | Keys to a Yacht (Which Yacht?) | L | 0.1–0.2 | 18,000–45,000 | 4 | 0.9 | keys_yacht | | Keychain says 'SS Tax Write-Off'. The yacht is somewhere. Probably. |
| `dino_egg` | Genuine* Dinosaur Egg | L | 6–9 | 20,000–50,000 | 4 | 0.8 | dinoegg | F | *Certified by the same guy who certified the moon rock. |
| `space_ticket` | Unused Space Tourism Ticket | L | 0.005–0.01 | 25,000–60,000 | 4 | 0.8 | ticket_space | | Seat 1A. Window. To SPACE. They had a brunch conflict. |
| `island_deed` | Deed to a Private Island (Very Small) | L | 0.01–0.02 | 30,000–80,000 | 4 | 0.6 | deed_island | | Island area: 4 m². Includes one palm tree and a sovereignty dispute. |
| `owner_statue` | Golden Statue of the Owner's Face | L | 24–30 | 20,000–55,000 | 4 | 0.5 | statue_face | | Commissioned for his 40th. Replaced for his 41st. Two-hand carry. |

The Trash-dex grows from 35 to **66 valuables**. `owner_statue` (24–30 kg) never fits the 18 kg bag cap on a normal pick, so it
only appears through a rich-theme guarantee swap, which doesn't check mass. `gold_toilet_seat` (16–22 kg) only fits a nearly empty
bag. Both are rare by design: heavy carries are a co-op and `work_gloves` L3 moment.

### 5.8 District non-valuables (21; builder D; tier = earliest district)

| id | Category | kg | Wt | T | Tags | Flavor |
|---|---|---|---|---|---|---|
| `kombucha_bottle` | Glass | 0.4–0.6 | 2.0 | 2 | | Tastes like a feeling you can't name. |
| `oat_milk_carton` | Paper | 0.1–0.2 | 2.0 | 2 | | Barista Edition. No barista was consulted. |
| `coffee_pod_pile` | Plastic | 0.2–0.5 | 2.0 | 2 | | Forty tiny cups of regret. |
| `scrapazon_box_mountain` | Paper | 2.0–4.0 | 2.0 | 2 | bulk | Somebody has a problem. It arrives in 0.3 seconds. |
| `trampoline_spring` | Metal | 0.2–0.4 | 1.0 | 2 | | Boing. Boing. Boi— (snapped). |
| `mlm_leggings` | Junk | 0.8–2.0 | 1.5 | 2 | bulk | Pattern: 'Galaxy Taco'. 400 pairs. |
| `participation_trophies` | Plastic | 0.5–1.5 | 1.0 | 2 | | Everyone's a winner. Everyone's in the trash. |
| `champagne_bottle` | Glass | 0.8–1.0 | 3.0 | 3 | | Popped for a Tuesday. |
| `shredded_tax_docs` | Paper | 2.0–4.5 | 2.0 | 3 | bulk | Nothing to see here. Literally. It's confetti. |
| `truffle_fry_cone` | Junk | 0.1–0.2 | 1.0 | 3 | | One fry left. It costs $6. |
| `wagyu_scraps` | Junk | 0.5–1.5 | 1.5 | 3 | | The cow had a massage schedule. |
| `espresso_capsules` | Metal | 0.2–0.5 | 2.0 | 3 | | Recyclable if you peel off the foil. Nobody peels off the foil. |
| `topiary_clippings` | Junk | 3.0–6.0 | 2.0 | 3 | bulk | Even the yard waste is pretentious. |
| `broken_croquet_mallet` | Junk | 1.0–1.8 | 0.8 | 3 | | Snapped in a 'friendly' match. Lawyers were involved. |
| `caviar_tin_empty` | Metal | 0.1–0.2 | 1.5 | 4 | | Licked clean by a man worth $4 billion. |
| `gold_leaf_pizza_box` | Junk | 0.5–1.0 | 1.5 | 4 | | The grease is still grease. |
| `ice_sculpture_puddle` | Junk | 3.0–6.0 | 1.5 | 4 | bulk | It was a swan. It is now a vibe. |
| `designer_shopping_bags` | Paper | 0.3–0.8 | 2.0 | 4 | | The bags cost more than your tires. |
| `smart_fridge_screen` | Hazard | 2.0–4.0 | 0.8 | 4 | | Still tells you you're out of oat milk. |
| `escooter_battery` | Hazard | 3.0–5.0 | 0.8 | 4 | | Lithium. Grumpy. VERY do not compact. |
| `yacht_flare` | Hazard | 0.3–0.6 | 0.8 | 4 | | Expired: either a dud or a firework. Find out (don't). |

### 5.9 Critters per district

- **v1 (data only):** `DistrictDef.critterMult` of 1.0 / 0.9 / 0.85 / 0.8 scales raccoon spawns and rabid bites. The Critters owner
  adopts it by reading `Districts.AtPosition(pos).critterMult`. Builders A–D do not edit spawn code.
- **Phase 2 roster (Critters owner; not simulated, keep each ≤ 2% of that district's daily income):** `yappy_doodle` (T2+; nips
  make you drop your bag), `peacock` (T3; steals shiny items from burst bags; honk to make it drop them), `guard_dog` (T3 gated
  lots; leashed, bonks trespassers), `hoa_inspector` "Mr. Pemberton" (T3; complaint letters), `monocle_raccoon` "Sir Reginald" (T4
  night; steals one treasure-bearing bag from an unattended hopper after 30 s), `guard_swan` (T4 moats; honks louder than your
  horn), `security_drone` (T4; films lawn driving), `pile_goblin_baron` (T4 neglected estates; drops a $500 tip when evicted).
  Consumables `critter_charcuterie` and `hoa_fruit_basket` ship with them.

---

## 6. Upgrade trees (Scrapazon)

Existing ids and saved levels keep their meaning: capacity 1–3, turbo 1, horn 1 and headlamp 1 give the same result as today.
**Rank** = the rank needed for that level. **Prereq** is written `id:level`. Each level has its own title and blurb (new
`ScrapazonItem.levelTitles/levelBlurbs`). Builder A writes 2–4 derpy reviews and a brand per new item, in the style of the
existing catalog. All numbers live in `UpgradeEffects` (§9.2); effects are applied idempotently from the level (on spawn, scene
load and `UpgradeChanged`).

### 6.1 TRUCK (dept `truck`, builder B)

| id | Lv | $ | Rank | Prereq | Title / blurb | Effect (exact) |
|---|---|---|---|---|---|---|
| `truck_capacity` | 1 | 150 | 1 | | *Hopper Extension Kit (+40 kg, Duct Tape Included)*: "Duct-taped sideboards. Holds 40 more kg of regret." | 200 kg |
| | 2 | 450 | 2 | | same | 240 kg |
| | 3 | 1,200 | 3 | | same | 280 kg |
| | 4 | 3,000 | 5 | | *Hopper Mk II: Compaction Ram*: "Squishes bags as they go in. Also squishes gloves. Keep hands out." | **340 kg**; a ram plate visibly thumps each accepted bag |
| `truck_turbo` | 1 | 300 | 2 | | *TURBO Garbage Truck Engine Swap (Flames Not Included\*)* (existing) | motorTorque **675** Nm/wheel, maxSpeed **12.5** m/s, steeringRangeAtMaxSpeed **6.7°**, brakeTorque 1,000 |
| | 2 | 2,800 | 4 | | *Twin-Turbo 'Stinkmaster' V8*: "Smells faster." | 800 Nm, **15** m/s, 4.7°, brake 1,100 |
| | 3 | 11,000 | 7 | | *Jet-Assisted Hopper Thrusters*: "Rear-mounted. Yes, the garbage gets warm." | 900 Nm, **17** m/s, 3.7°, brake 1,200, lateral limit **7 m/s²**, `ContinuousSpeculative` collision |
| `truck_handling` | 1 | 250 | 2 | | *Sticky Tires + Unflip Button*: "For when physics wins." | sideways friction stiffness ×1.15, `centreOfGravityOffset` −1.0 → −1.3, **free, instant unflip** (hold R 0.5 s; no tow fee) |
| | 2 | 2,000 | 4 | | *Racing Suspension (Tuned by a Guy Named Tony)*: "Tony says it's fine." | sideways ×1.30, forward ×1.15; anti-tip assist below 12 m/s (counter-torque once roll > 25°) |
| | 3 | 9,000 | 8 | | *Drift Kit for Garbage Trucks*: "Tokyo Drift, but it smells." | sideways ×1.40; hold **Space** while driving = handbrake drift (rear sideways stiffness ×0.45, rear brake 1,500 Nm) |
| `truck_arm` | 1 | 1,800 | 3 | `truck_handling:1` | *Grabby Arm™ Side-Loader (Press X to Grab)*: "It has one job and a 95% success rate. That's an A." | while stopped (< 0.5 m/s) hold **X**: grabs resting curb bags on the **right** within **2.4 m** of the truck's side (hug the curb); deploy 3 s, then **2.5 s per bag**; 5% fumble (the bag bonks off the roof onto the road, never bursts) |
| | 2 | 9,000 | 6 | `truck_arm:1` | *Grabby Arm 2: Crawl & Grab*: "Never leave the cab again. Your legs will atrophy. Worth it." | **X toggles AUTO**: both sides, while ≤ **3 m/s**, reach **3.2 m**, deploy 1.2 s, **1.6 s per bag**, no fumbles |
| | 3 | 39,000 | 10 | `truck_arm:2`, `truck_capacity:4` | *Octo-Arm 9000 (Both Sides, No Mercy)*: "Eight arms' worth of grabbing in two arms. Don't ask." | AUTO, both sides, ≤ **4 m/s**, reach **5.0 m** (drive the centre line, clear both curbs), deploy 0.5 s, **0.9 s per bag**, plus **Litter Vac**: loose items within 3 m are sucked in (`TruckHopper.AddLooseToTruck`) |
| `air_horn` | 1 | 120 | 1 | | *Industrial Air Horn (Scares Raccoons, Neighbors, God)* (existing) | scare radius 12 → 25 m |
| | 2 | 1,200 | 6 | | *Foghorn of the Gods*: "Audible from space. Several complaints from space." | **40 m**; nearby hedges and trees shake (cosmetic). Goblins still only get grumpy (existing behaviour). |
| `truck_headlights` | 1 | 200 | 2 | | *Brighter-ish Headlights*: "Now 40% less 'is that a raccoon or a bag?'" | headlight range ×1.6, intensity ×1.5; raccoons in the beam within 20 m get `Critter.Scare(truckPos, false)` (once per critter per 10 s) |
| | 2 | 4,500 | 6 | | *Stadium Light Bar*: "Turns night into a Tuesday afternoon." | roof light bar; beam scare out to 30 m; critters entering 8 m of the truck are scared |

**Arm rules:** it only takes `Resting` curb bags with no `Holder`, not in scripted flight, that fit (`GameSession.CanFitInTruck`).
Grab: `bag.SetInFlight()`, then the arm drives the pose (renderer-only primitives, **no colliders**), then `bag.TossArcInto(hopper,
0.35f)`. Everything else (fee, curb bonus, record removal, "Curb clean!", FULL toast) flows through `TruckHopper.Accept` as
today. The decision of which bag to grab runs on the host. It aborts if a player grabs the bag mid-flight. **Co-op
(`Multiplayer.md` §4.3):** when the shotgun seat is occupied, the arm is operated by the shotgun rider and AUTO is off. Keep it slower
than a skilled player on foot until L3; the sim's arm share was 60% of stops at L1.

**Truck recovery (base game, not an upgrade; producer M5):** when the truck has been upside down or stuck (< 0.3 m/s with
throttle, for 3 s), "Hold R: Call a Scrapazon Prime Tow (−$25)". Holding R for 1.5 s makes a crane drone right the truck at
the nearest road point. `truck_handling` L1 makes it free and 0.5 s.

**Engine note:** the tier values are absolute, not multipliers. Stock (level 0) also changes: **brakeTorque 2000 → 1000** per
wheel, because today's brakes ask for 2.1 g and lock the wheels. Hard cap is **18 m/s**. L3 needs the additive cornering limit in
`TruckControllerV2` (§9.6, B). If that edit is refused, L3 becomes 15 m/s plus a 3 s Shift boost (20 s cooldown), and nothing else
changes. The existing 6WD torque is intentional; don't "fix" `WheelControl.motorized`. Restart play mode before tuning physics
(the live Editor had `fixedDeltaTime` stuck at 1e-4 from a test).

### 6.2 GARAGE (dept `garage`, builder C)

| id | Lv | $ | Rank | Prereq | Title / blurb | Effect (exact) |
|---|---|---|---|---|---|---|
| `auto_tipper` | 1 | 600 | 3 | | *Auto-Tipper (Lever Pulls Itself, Creepily)*: "It knows when you're home." | TIP IT pulls itself once `TruckUnloader.IsReady` and the truck has been under 0.2 m/s for 1.5 s (`unloader.OnInteract(null)`). Unload time 10 s → 3 s (bag interval 0.1 → 0.03 s). |
| | 2 | 2,500 | 6 | `conveyor_belt:1` | *Tip-O-Matic Direct Feed*: "The floor has never been cleaner. The floor is sad." | tipped bags go straight onto the Bag Belt (overflow goes to the pile) |
| `conveyor_belt` | 1 | 1,500 | 4 | | *Sorting Conveyor Belt (Automatic\*, Loud)* (existing): "Finally back in stock (1997–2026)." | **Bag Belt**: bucket elevator at (5.3, −0.6), overhead belt along z −0.4 at y 3.3 to x −0.9, a derpy slide down to a **tray at (−1.2, 1.0, 2.55)** by the table. It pulls a pile bag every 2 s. R "Next bag!" takes the tray bag instantly. Tray bags open on the slam in 0.6 s (instead of 2.5). Feeds Magnus. |
| | 2 | 9,000 | 6 | `conveyor_belt:1` | *Item Belt Add-On*: "The trash comes to you. Like a sushi bar. Of trash." | **Auto-slitter**: when the table holds < 10 items and nobody is mid-rip, the tray rips its bag onto the table (`SortingTable.Accept`), spilling toward the bins. Per-item sort time ×0.85. |
| | 3 | 29,000 | 9 | `conveyor_belt:2` | *Twin Turbo Belt (Loud)*: "LOUDER." | two lanes (2× feed); per-item time ×0.80; Magnus speed ×1.3 |
| `sorting_table` | 1 | 700 | 3 | | *XL Sorting Table (Fits 2 Bags + a Sandwich)* | table top 3.0 → 3.8 m, `maxItemsOnTable` 40 → 60, item time ×0.95 |
| | 2 | 6,000 | 6 | | *Double-Wide Table w/ Cupholders & Magnet Strip*: "Metal just... leaves." | the magnet strip slides one Metal item every 1.5 s into the METAL bin (a correct sort; no combo) |
| `compactor_mk2` | 1 | 900 | 3 | | *Compactor Mk II: Auto-Bale*: "It's hungry on its own schedule now." | auto-compact when the chamber is ≥ 80% full; faster bulk processing (0.3 s per bag); chamber and bulk pay **$0.25/kg** instead of $0.20/kg |
| | 2 | 2,500 | 5 | | *Hazard Sniffer Nose*: "Achoo means no." | dampens comic hazard bangs for quieter compacting; hazards are consumed once, not ejected for reuse; the "Allow Kaboom" toggle is kept for clips; chamber and bulk pay **$0.30/kg**. No compaction fees at any level. |
| | 3 | 8,000 | 9 | `compactor_mk2:2` | *Treasure Rescue Grate*: "RIP 'RIP Wedding Ring' toasts." | valuables survive compaction and pop into the KEEP crate **dented** (value ×0.30, note "Survived the compactor"); chamber and bulk pay **$0.35/kg**, below every base recycling rate |
| `recycling_contracts` | 1 | 300 | 3 | | *Glassy McGlassface Co.*: "A guy who buys your recycling. He seems legit." | recycling pay ×**1.5** (hand and Magnus, after the combo) |
| | 2 | 1,500 | 5 | | *Tin Can Tony's Metals* | ×**2** |
| | 3 | 4,000 | 8 | | *Big Plastic (Not That One)* | ×**3** |
| | 4 | 11,500 | 11 | | *The Cardboard Cartel* | ×**4** |
| `hazmat_contract` | 1 | 300 | 4 | | *Hazmat Disposal License (Laminated)* | hazard pay $0.75 → **$1.50** per item |
| | 2 | 2,500 | 7 | | *E-Waste Buyback Program* | **$4.00** per item |
| `trophy_shelf` | 1 | 1,000 | 4 | | *Trophy Shelf (Holds 6 Finds, Judges You)* | shows 6 of your best kept finds over the sort station (props from `ItemVisuals.Build`; they can still be sold; fancier displays get first pick, §6.8); **+40 Rep** per midnight |
| | 2 | 6,000 | 8 | | *Museum Wing (24 Plinths + Velvet Rope)* | 18 more plinths above it (24 in all); **+160 Rep** per midnight |

Garage geometry (measured): tipping pile x 5.0–6.5, z −4.3..−2.1; lever (4.45, −1.56); truck bay x −3.1..2.9, z −3.8..−1.2
(roof y ≈ 2.9); Magnus rail x 2.83–3.73, z −2.98..1.05 (arm envelope up to y 2.64); table centre (−3.25, 2.55), top y 0.88; bins
row z 3.39; free overhead corridor z −1.2..1.5, y 2.8..4.3 (the only obstacle is the east pillar x 6.05–7.35, z −1.05..0.25). Belt
bags are **scripted carriers** (waypoint path, belt speed 0.8 m/s, 1.5 m spacing), not physics. On capture or reload,
`SceneTrashKeeper` must not drop them from 3.3 m: re-adopt bags inside the path volume, or send them to the tray. The trophy shelf
goes on a free wall visible from the sort station; never block the walkway z −1.2..1.5. The display line's spots and the checks
behind them are in §6.8 (walls: north z 4.91, south z −5.00, west x −6.92; the south pillars' faces at z −4.38).

### 6.3 MAGNUS (dept `magnus`, builder C): a career path

> **Reworked 2026-10-01** after a playtest: the first Magnus item on sale was the $500 rail motor (a speed upgrade with nothing
> to speed up), and the first item that did anything (brain L1) needed rail L2 + gripper L1 + conveyor L1 at rank 5. Now the
> department is **one ordered career path** (`Progression/MagnusCareer.cs`, `MagnusCareer.Steps`). Step 1 gives Magnus his job,
> every later step adds exactly one thing, and every step raises his chess tier. The ids are unchanged (saves keep working);
> the levels mean new things (old saves: rail L2 clamps to 1, gripper L3 to 2, and an old gripper L1 now reads as the pincer).

| Step | id | Lv | $ | Rank | Prereq | Title / blurb | Adds (exact) |
|---|---|---|---|---|---|---|---|
| 1 | `magnus_brain` | 1 | 500 | 2 | | *Magnus Gets a Job (Rail Extension + Kitchen Tongs + Brain Module 1)*: "He's always wanted a commute. And a purpose. Mostly the commute." | **His job.** A straight rail branch from the chess stop (3.28, 0.72) to a **Sort Stop at (−3.4, 0, 1.2)** (every table item within 2.1 m of his claw), taped **kitchen tongs** (table items ≤ **5 kg**) and **Brain Module 1**: he sorts the **recyclables** (4 bins) and **junk** (into the compactor) lying on the table at **8 items/min, 85%** accurate, 06:00–22:00. You rip the bags onto the table; valuables and hazards go to LEFTOVERS. **No conveyor needed** (he works the table; the Bag Belt only feeds it). |
| 2 | `magnus_rail_motor` | 1 | 300 | 3 | step 1 | *Magnus Rail Motor (Now With Racing Stripes)*: "Racing stripes make it go faster. That's science." | **Speed:** rail 0.7 → 1.4 m/s (the commute halves) and work speed ×**1.25**. Racing stripes and a tiny spoiler. |
| 3 | `magnus_gripper` | 1 | 1,200 | 4 | step 1 | *Heavy-Duty Lobster Pincer*: "Pinch. Pinch. PINCH." | **Heavier items + rips bags:** table items ≤ **25 kg**, and he feeds himself: the Bag Belt tray's bag first, else the next tipping-pile bag (vision-flagged first, then the top of the heap) lobs across the garage into his claw ("YOINK!", the same lob as the station's "Next bag!") and he SLAMS it open on the table. He waits for each spray to land and stop rolling (≤ 4 s) before the next rip, and only rips while the table holds fewer than **14** items (×MaxItems/40 with the XL table, never above max − 8; LEFTOVERS count). |
| 4 | `magnus_vision` | 1 | 4,000 | 5 | step 1 | *X-Ray Googly Eyes*: "He sees everything. He judges most of it." | **Treasure flags:** **60%** of treasure-bearing bags on the tipping floor get a gold glint and "!" (deterministic by bagId). "Next bag!", the belt and Magnus take flagged bags first. |
| 5 | `magnus_brain` | 2 | 12,000 | 7 | step 4 | *Brain Module 2: Treasure Sense*: "He can smell bling." | **Treasure:** valuables into the KEEP crate instead of LEFTOVERS; **12/min, 92%** |
| 6 | `magnus_vision` | 2 | 18,000 | 8 | step 5 | *Appraisal Monocle HUD*: "Classy. Smug. Correct." | **100%** flagged, glint in rarity colour; ebae listings default to Fair, realised prices +3% |
| 7 | `magnus_brain` | 3 | 40,000 | 9 | step 3 (+ step 5) | *Brain Module 3: Hazmat Certified*: "Now legally allowed to touch batteries." | **Hazards:** into the barrel instead of LEFTOVERS (he now handles everything); **18/min, 96%** |
| 8 | `magnus_gripper` | 2 | 16,000 | 10 | step 3 | *Quad-Claw (Like the Arcade, But It Actually Grabs)*: "Finally, a claw machine that works." | **Speed:** work speed ×**1.4** |
| 9 | `magnus_brain` | 4 | 60,000 | 11 | steps 2, 6, 7, 8 | *Grandmaster Module (Also Plays Chess Now. Better.)*: "The final form. He's been waiting." | **24 h:** works around the clock; **30/min, 99.5%**; MAGNUS PRIME at chess |

Total **$152,000** (was $136,400 over 10 levels). The prereqs keep the path mostly linear but let a player swap neighbouring
steps (the sim's typical player buys 8 before 7). Prices come from the balance sim (§8, "Magnus career"): every step is cheap
next to the income at its rank gate (step 1 is about one day's net at rank 2), and the late steps are priced up so the full tree
still lands at 13.5 h (it was 13.2 h).

**Scrapazon shows it as a career** (A/C, `Shop/ScrapazonMagnusPath.cs`, `ScrapazonApp`): the Magnus department opens with a strip
"Magnus's Career: N of 9 steps done", the 9 steps as tiles (done / next / buyable / locked), a **"Magnus can now:"** box written
from the live levels (`MagnusCareer.CanNow()`) and a **"Next: Step k of 9"** box with what it adds, its price and either a buy
button or exactly why it's locked ("Requires Rank 5: Senior Sack Specialist", "Requires Step 4: X-Ray Googly Eyes", "Comes after
Step 5: Treasure Sense"; "Comes after" names the level right before it on the same part, so Hazmat on a fresh save says Step 5,
not Step 1). Magnus cards sort in career order (and re-sort after a purchase, once the delivery animation is done), say "career
step k of 9" and lead with what the step adds; lock reasons name career steps (`Progression.CanBuy`); the purchase toast names the
step ("Career step 9 of 9: Grandmaster Module! Magnus now ...") and says what's next. The Career page's Magnus card shows "Career:
N of 9 steps" and links to the department.

**The Magnus sorter (C, `Garage/MagnusSorter.cs`):**
- **When he works:** he goes on shift once he has the job (brain ≥ 1), it is within his hours, the station is empty, no human is
  seated at chess, and there is work: table items he can handle, or (from step 3) a tray or pile bag he can fetch while the table
  has room. He drives chess stop → Sort Stop (about 18 s at 0.7 m/s, 9 s at 1.4 m/s).
- **Stepping aside:** a player entering the station sees "Magnus is on shift. [E] shoo him"; he drives back and plays chess as
  today. `MagnusChessBot.ServerLoop` already stops playing when `!track.AtChess`.
- **Picking:** from `LooseItem.All` where `table.IsOnTop`, `Resting`, reachable (`CanReach`), grip size from `LooseItem.Box`.
  He carries each item 0.6 m above toward its bin, then calls `bin.Accept(li)` (bins arc items in from anywhere). He uses
  `MagnusArm.PlayPlan` with a new `Step.onGrab` that calls `li.SetInFlight()`. Items over his grip limit (5 kg tongs, 25 kg pincer)
  are left with a "too heavy" line.
- **Spray and the floor:** after a rip he waits until nothing near the table is still flying or rolling (`MagnusSorter.
  WaitSpraySettled`, ≤ 4 s), rips only into a table with fewer than `ripTableLimit` (14) items, and while he's on shift the
  sort station's floor rescue runs for him (`SortStation.RescueFloorItems`): anything that bounced onto the floor around the
  table hops back onto it after resting 1.5 s ("Floor is not a bin. Hup!"). Floor items also count as work, so he clocks in for
  them. (Before 2026-10-01 he ripped bags back to back into the still-bouncing spray and the floor items piled up.)
- **Mistakes:** with probability (1 − accuracy) the item goes to a wrong recycling bin: normal contamination (−$0.30, −2 Rep)
  and a line like "My bad. Knight to trash-6."
- **Speed contract:** effective items/min = brain rate × rail motor (×1.25) × quad-claw (×1.4) × conveyor L3 (×1.3), within ±10%:
  8/min with the job alone, 68/min at the very top. The arm is about 11 s per item at ×1, so the top needs `SpeedMultiplier` ≈ ×15.6
  (cap 16). The number is the contract; the animation can flex.
- **No combo for robots:** robot sorts must not feed the player's combo (`SortCombo.RegisterCorrect`). Add an `auto` flag.
- **Networking:** host decides; broadcast (item uid, bin) like `PlayPlanClientRpc`.

**Chess strength: every career step raises the tier; the job and the Grandmaster Module raise it by 2** (the job brings three
parts; the Grandmaster is the chess chip): tier = rail (0–1) + gripper (0–2) + vision (0–2) + brain (0–4) + 1 with the job + 1
with the Grandmaster Module = 0..11 (`UpgradeEffects.MagnusChessTier` / `ChessTierFor`). Add public `ThinkSeconds`, `MaxDepth`
and `BlunderChance` to `MagnusChessBot`, and a public `Searcher.MaxDepth` used as the iterative-deepening bound (one line; don't
poll `CurrentDepth`). A blunder plays a random legal move with the `Shrug` gesture, only when a human is playing. Self-play uses the
same tier. It is server-only, so it is co-op safe.

| Tier | Think s | Max depth | Blunder | Label (joke, not measured) | Reached at (typical, sim seed 1) |
|---|---|---|---|---|---|
| 0 | 0.05 | 1 | 40% | "Factory Settings (~300 Elo)" | start |
| 1 | 0.08 | 2 | 30% | ~450 | (an old save's rail motor without the job) |
| 2 | 0.12 | 2 | 22% | ~600 | step 1, the job: 0.75 h |
| 3 | 0.15 | 3 | 16% | ~800 | step 2: 2.9 h |
| 4 | 0.20 | 3 | 11% | ~1000 | step 3: 4.25 h |
| 5 | 0.30 | 4 | 7% | ~1200 | step 4: 4.4 h |
| 6 | 0.40 | 4 | 4.5% | ~1400 | step 5: 7.3 h |
| 7 | 0.60 (today's) | 5 | 2.5% | ~1600 | step 6: 7.7 h |
| 8 | 0.80 | 6 | 1.2% | ~1800 | step 8: 8.1 h |
| 9 | 1.00 | 7 | 0.5% | ~2000 | step 7: 8.75 h |
| 10 | 1.50 | uncapped | 0.2% | ~2200 | (skipped: the Grandmaster Module adds 2) |
| 11 | 2.50 | uncapped | 0% | **MAGNUS PRIME** | step 9: 9.6 h |

**Banter** (speech bubble or toast) by tier band:
- **0–2:** "Horsey go jump." / "I have placed your pawn in RECYCLING. Wait. That was my queen." / on losing: "GG. I was sorting you
  a victory."
- **3–5:** "Your bishop is Junk category." / "I see a fork. I also see a spoon. Mostly the spoon." / "Check! Also your plastic bin
  is 80% full."
- **6–8:** "Mate in 4. Please take out the trash in 3." / "I calculated 40,000 positions. 39,000 involved raccoons."
- **9–10:** "I have sorted every possible future. You lose in all of them. In one you also get rabies."
- **11:** "I AM THE COMPACTOR OF KINGS." / on a rare loss: "...I am going to go sort something. Alone."

**Upgrade visuals** (`Setup_GarageUpgrades`, gated by `GarageUpgradeVisuals`, which also takes an optional second condition):
the rail branch, the MAGNUS PARKING box and the LEFTOVERS tape come with the job (brain ≥ 1); taped kitchen tongs show from the
job until a gripper replaces them (brain ≥ 1 AND gripper = 0); lobster claw = gripper L1; arcade quad-claw = gripper L2; rail
motor = stripes and a spoiler; vision = huge googly eyes with a scan beam, then a monocle; brain = a jar-brain dome that grows each
level, with a graduation cap at L4.

### 6.4 PERSONAL GEAR (dept `gear`, builder B)

| id | Lv | $ | Rank | Title / blurb | Effect (exact) |
|---|---|---|---|---|---|
| `work_gloves` | 1 | 150 | 1 | *Grippy Work Gloves*: "Grip strength of a raccoon. Smell of a raccoon." | carry slowdown −50% (`TrashCarrier.maxSlowdown` 0.45 → 0.225); `maxThrowSpeed` 14 → 15.5 m/s |
| | 2 | 1,200 | 5 | *Double-Fisted Gloves (Carry 2 Bags!)* | **Back Sack**: pressing E on a second bag while carrying one stashes the first on your back; one E at the hopper tosses both. Built as a player-side `IInteractionProvider` plus public fields, with **no `TrashCarrier` edits**. Fallback if that proves impossible: hopper toss range 2.8 → 4.5 m. |
| | 3 | 4,000 | 8 | *Gorilla Grip Gauntlets* | no carry slowdown at all; the Back Sack holds 2 (3 bags in total) |
| `work_boots` | 1 | 200 | 2 | *Squeaky Sneakers of Speed* | walk and sprint +10% (`MoveSpeed` 3 → 3.3, `SprintSpeed` 5.3 → 5.83) |
| | 2 | 1,500 | 6 | *Spring-Loaded Stompers* | +20%; **Ctrl** stomp scares critters within 4 m (`Critter.Scare`), 6 s cooldown |
| `headlamp` | 1 | 80 | 1 | *Night Shift Headlamp* (existing) | on at night |
| | 2 | 800 | 6 | *UV Treasure Headlamp* | at night, **35%** of curb bags that hold a valuable glow faintly purple within 15 m (deterministic by bagId) |
| `treasure_wand` | 1 | 1,500 | 4 | *Metal Detector Wand (Beeps at Bling)* | picking up a bag beeps if it holds a valuable: **45%** detection, deterministic by bagId, never a false positive |
| | 2 | 12,000 | 7 | *Quantum Bling Wand* | **80%**; also beeps when you walk within 2 m of such a curb bag |
| `rabies_vaccine` | 1 | 500 | 4 | *Pre-Exposure Rabies Vaccine* | infection per rabid bite 50% → **25%** (`GameSession.InfectionResist01` = 0.5) |
| | 2 | 2,000 | 7 | *Rabies Booster (Immune, Smug)* | **immune** (resist 1.0); still knocked back |
| `hard_hat` | 1 | 300 | 3 | *Hard Hat (Bonk-Rated)* | critter bite/bonk knockback distance ×0.5; 50% chance you keep holding your bag. Chuck's hard-hat look is a later cosmetic (`Character.md` §14 Bert parts). |

### 6.5 BUSINESS (dept `business` and `permits`)

| id | Lv | $ | Rank | Prereq | Title / blurb | Effect (exact) | Builder |
|---|---|---|---|---|---|---|---|
| `permit_minivan_meadows` | 1 | 600 | 4 | | *Minivan Meadows Collection Permit (Laminated, Slightly Sticky)* | opens the Meadows Gate; Trust starts at 40% | A (item), D (gate) |
| `permit_snootington_hills` | 1 | 3,500 | 7 | | *Snootington Hills Collection Permit (Embossed, Smells Like Old Money)* | opens the Snootington Gate | A, D |
| `permit_billionaire_bluffs` | 1 | 12,000 | 10 | | *Billionaire Bluffs Collection Permit (Signed by a Butler)* | raises the boom barrier | A, D |
| `ebae_power_seller` | 1 | 1,200 | 4 | | *ebae Power Seller Badge* | fee 8% → **5%**; +1 buyer per listing | A |
| | 2 | 9,000 | 7 | | *ebae Bulk Lister Pro* | fee **3%**; offers arrive 2× faster | A |
| | 3 | 29,000 | 10 | | *ebae Diamond Seller (Auto-Accept)* | fee **1%**; auto-accepts any offer ≥ 97% of appraisal | A |
| `hire_kevin` | 1 | 600 (+$40/day) | 4 | | *Hire Kevin (Part-Time, Full-Chaos)*: "He lost a shoe. On day one." | Kevin covers **60%** of Mildly Pleasant's houses | D |
| | 2 | 7,000 (+$250/day) | 7 | `hire_kevin:1` | *Kevin + Other Kevin* | 100% of MP + **50%** of Meadows | D |
| | 3 | 23,000 (+$900/day) | 10 | `hire_kevin:2` | *The Kevins (Unionized)* | 100% of MP and Meadows + **40%** of Snootington | D |
| `golden_truck` | 1 | 140,000 | 12 | `truck_turbo:3`, `truck_arm:3`, `key_to_the_city` | *The Golden Garbage Truck (24K, Street Legal-ish)* | gold paint, sparkle trail, fanfare horn, curbs cheer as you pass; top speed 17 → **18 m/s** (the hard cap). Delivered by a swarm of Scrapazon drones. | A (gate), B (truck) |

**Kevin's Crew (D, `Trash/KevinCrew.cs`, data only, host only):**
- **His houses:** Kevin serves the first ⌈coverage × subscribed houses⌉ of each covered district, ordered by
  `StableHash("kevin" + stopId)`.
- **Sweeps:** at **08:00, 14:00 and 20:00** (Bluffs isn't covered). He collects every curb bag at his houses.
- **Pay:** fee × pay fraction (district windows) × **0.70**, via the Ledger ("Kevin collected 14 bags on Maple St. He says hi.").
  5% of his bags burst on the way: no fee, gone.
- **Rep:** half the bag Rep, via a `BagCollected` fact with `byKevin = true`.
- **Deliveries:** his bags go into `GameSession.DepotDeliveries`. They appear on the tipping floor at 18:00, or on garage entry
  (C spawns them; physical pile cap 40 bags, the rest wait in the list).
- **Wage:** charged at midnight. The player can still grab Kevin's houses' bags first.
- In co-op, real friends replace him.

### 6.6 CONSUMABLES (dept `pharmacy` and `gear`)

| id | $ | Rank | Pocket cap | Effect | Builder |
|---|---|---|---|---|---|
| `rabies_shot` | 35 | 1 | 5 | existing | — |
| `repellent` | 20 | 1 | 9 | existing | — |
| `garbage_juice` | 15 | 1 | 5 | *Garbage Juice™ Energy Drink*: +25% move and carry speed for 2 in-game hours; hiccups | B |
| `lucky_gnome` | 150 | 3 | 3 | *Lucky Garden Gnome (Rub Belly, Receive Bling)*: for 24 in-game hours, bags put out in tier 1–2 districts roll with valuable chance ×1.25 | D (roll), A (item) |
| `golden_gnome` | 7,500 | 9 | 2 | *GOLDEN Garden Gnome (He Knows What He's Worth)*: 24 h, all districts ×1.40, and each Rare roll has a 10% chance to become Epic | D, A |

Gnomes aren't in the sim. They are priced at about break-even for their 24 h, so they are fun and help Trash-dex hunting without
shifting pacing. Timers are `GameSession` stamps (`SetStamp("lucky_gnome_until", TotalHours + 24)`).

### 6.7 COSMETICS (dept `cosmetics`; optional sinks; cut first if time is short)

`truck_paint_primer` $250 (r2), `truck_paint_flames` $1,500 (r5), `truck_paint_raccoon_camo` $4,000 (r7), `truck_paint_chrome`
$15,000 (r9) (B); `horn_pack_musical` $600 (r4) and `trash_fm_radio` $300 (r3) (B); `garage_disco_ball` $1,200 (r5, Magnus dances;
C). Hi-vis vest colours wait for Chuck's player colours and, later, `Multiplayer.md`'s Pocket money. Cosmetics are **not** part of
the "full tree" pacing. They are the intended filler for the Golden Truck save-up.

### 6.8 DISPLAYS (dept `garage`): show off your finds

> **Added 2026-10-01** after a playtest ask for "different options for purchasable shelves (cheap) or display cases (expensive),
> some nice ones for your valuables". The trophy shelf became one piece of a display line (`Garage/DisplayPlanner.cs`).

Kept finds (`GameSession.Inventory`: the valuables you haven't sold or listed) show themselves on the display pieces you own,
**best find in the best piece**: rarity first, then appraisal. Listing or selling a find on ebae takes it out of the inventory, so it
leaves the display (a dust puff; the next find moves up). Nothing is locked away: displayed finds can still be sold any time.

| id | $ | Rank | Slots | Where (ShopScene) | Rep per midnight | Title / blurb |
|---|---|---|---|---|---|---|
| `display_plank_shelf` | 150 | 1 | 3 | north wall over the bale corner, left of the trophy shelf (x −5.97..−4.93, y ≈ 1.9–2.6) | 0 ("nobody is impressed by a plank") | *Wobbly Plank Shelf (Holds 3 Finds, Barely)*: "One plank. Two nails. Infinite confidence." |
| `trophy_shelf` L1 | 1,000 | 4 | 6 | north wall over the sort station (unchanged, §6.2) | +40 flat | *Trophy Shelf (Holds 6 Finds, Judges You)* |
| `trophy_shelf` L2 | 6,000 | 8 | +18 | three velvet rows and "MUSEUM OF FINE GARBAGE" above it | +160 flat (in all) | *Museum Wing (24 Plinths + Velvet Rope)* |
| `display_glass_case` | 2,500 | 6 | 4 | south wall by the garage door (x −6.47..−5.27, 0.46 m deep), two glass levels, interior lights | +10 while it shows a find, + Showcase | *Lit Glass Display Case (Fingerprints Sold Separately)*: "Like a jewelry store. In a garage. Next to a garage door." |
| `display_pedestal` | 9,000 | 9 | 1 | south wall between the case and the SW pillar (x −5.14..−3.85): marble, velvet rope on the pillars' face line, its own spotlight | +25 while it shows a find, + Showcase ×2 | *Pedestal of Honor (Velvet Rope + Spotlight)*: "For your single greatest find. Do not touch. Do not breathe on it." |
| `display_vault` | 22,000 | 11 | 6, Epic+ only | north wall between the north pillar and the office (x −0.58..2.64, 0.45 m deep), straight across the room from the truck bay: 2 rows of 3 gold-lit niches on the right (x 0.51..2.61), the giant round door swung open flat on the left beside the pillar, the sign over the niches under the Bag Belt (y 2.6–3.04) | +40 while it shows a find, + Showcase | *The Legendary Vault (Epic+ Only, No Riff-Raff)*: "A bank vault for your best garbage. The door weighs more than your truck." |

- **Hand-out order:** pedestal (1) → vault (6; Epic and Legendary only, other niches wait empty) → glass case (4) → trophy shelf (6)
  → museum wing (18) → plank (3). 38 slots in all; more finds than that just stay in the inventory. Which finds a piece shows is
  the plan; where on the piece is presentation: a find that stays on a piece keeps its spot, so a new best find moves one find
  per piece down the line instead of reshuffling every shelf.
- **Showcase Rep (the fancy-case perk):** every find shown in the glass case, the pedestal or the vault pays **Rare +2, Epic +4,
  Legendary +8** Rep per midnight; the pedestal's spotlight doubles its find (a Legendary there: +16). Base Rep is paid only while
  the piece shows at least one find. It's all paid at midnight as `trophy_shelf` Rep: `UpgradeEffects.TrophyRepPerDay` = the trophy
  shelf's flat +40 / +160 + `DisplayPlanner.ExtraRepPerDay()`, so `ProgressionRules` didn't change. The most the new pieces can pay
  is 75 base + 96 Showcase = **171 Rep a day**, about 6% of late-game Rep income (≈ 2,600 a day); before the top district (rank
  10) only the glass case and the pedestal can pay anything (≈ 15–70 a day).
- **Look at it (placard):** aim the camera at a displayed find from within 5.5 m (camera within 9 m) and a museum placard pops up
  just above it in view: rarity, name, "Found at <address>, day N", the engraving (`ItemInstance.note`) or the item's flavour line,
  condition if dented, the appraisal, the Showcase Rep it earns and which piece it's on. An empty slot says why ("Epic and
  Legendary finds only. No riff-raff." / "Keep a find (don't sell it) and it shows up here."). The card sits part of the way along
  your line of sight, so the recycling banner or a parked truck can't hide it. Every find also gets a small brass name plate.
- **Arrange (stretch goal, built):** stand in front of a piece (its `InteractionZone`), look at a slot and press **E**. Looking
  straight at a slot from within 2.2 m (flat) lifts the piece's zone score to 3 (`DisplayGallery.arrangeFocusScore`), above
  the garage door's 1 and line-of-sight picks (0–2) but below the sort station's step-up (4), so at the glass case and the
  pedestal E is "Swap find" and F keeps "Exit Garage" (look away and E is the door again); further away it's **F** when
  another interactable has E. The prompt is just "Swap find" / "Back to auto" (one tooltip row) and the placard's last line
  names the next find ("Swap find: Vinyl Record next"). The slot pins the next find in best-first order (the one it pushes out
  moves down the line); after the last find a pinned slot goes back to automatic, and an automatic slot starts over from the best
  find (finds keep their spot when a pin clears, so without that the slot would get stuck). Pins are company counters `display_pin_<piece>_<slot>` = a
  32-bit tag of the find's uid (`DisplayPlanner.PinTag`); a pin whose find was sold is ignored, and the vault still refuses anything
  below Epic. Guests don't get the prompt (they never write the company, `Multiplayer.md` rule 16).
- **Juice:** an arrival pops in with a dust puff, a rarity-coloured sparkle and a "pop" (Rare+ adds a sparkle sound). The piece
  reacts: the plank seesaws on its one nail (and sways a little all the time), case and pedestal lights flare, the vault door
  wiggles on its hinge with a squeak. A Legendary arriving makes a tiny crowd gasp: "ooooh!", "oooooh...", "aaah!" float up with
  the legendary sting (a real crowd clip can be registered as `sfx/ooooh` later). Finds turn slowly (the pedestal's also floats);
  buying a piece pops it in with the usual garage-upgrade puff.
- **Layout checks** (overlap probes on a scratch copy of ShopScene with every garage upgrade shown, `mdd/displays/preview.cs`):
  nothing on the south wall reaches past the pillars' faces (z −4.38), so the truck's path from the west door to the bay
  (z −4.2..−0.6) and the bay itself stay clear; nothing touches the walkway (z −1.2..1.5), the dump zone, the Magnus rail and arm
  envelope, the office, the bale-corner floor, the conveyor slide on the north wall or the sort station. The vault sat between
  the south pillars until 2026-10-01, where the parked truck hid everything but its sign; it now hangs on the north wall
  (renderer and collider overlap probes with every upgrade shown, the Bag Belt included: 0 hits; its Arrange zone touches no
  other zone). Slot visibility with the truck parked (raycasts, `scratchpad/mdfix/vis.cs`): the vault 6/6 from the spawn, the
  walkway, the sort station, the west door and the chess table; the glass case and pedestal (south wall, west of the SW pillar)
  are seen from the sort station and the west half of the garage, but not from east of the truck. Each piece has a solid
  collider except the wall shelves.
- **Co-op:** the plan is a pure function of company state (inventory, upgrade levels, pin counters), so every machine shows the
  same displays with no new network code. Only the host pays Rep (midnight) and only the host or a solo player writes pins.
  Props, placard and juice are local presentation. No new RNG; the gaze uses `Camera.main` and `SortingKit.TryGetLocalPlayer`.
- **Files:** `Garage/DisplayPlanner.cs` (rules), `Garage/DisplayPiece.cs` (one piece: props, plates, juice, Arrange),
  `Garage/DisplayGallery.cs` (refresh, gaze placard, "ooooh"), `Garage/DisplayPlacard.cs`, `Garage/TrophyShelf.cs` (now a
  `DisplayPiece`), `Editor/Setup_Displays.cs` (menu *Rubbish/Setup/GarageDisplays*, root `GarageDisplays` in ShopScene; it also
  removes an old `GarageUpgrades/TrophyShelf`, which `Setup_GarageUpgrades` no longer builds), numbers in `UpgradeEffects`
  (`*Slots`, `*Rep`, `ShowcaseRep`, `PedestalShowcaseMult`, `VaultMinRarity`).
- **Scrapazon:** each piece has its own product art (`ScrapazonArt.PlankShelf/GlassCase/Pedestal/Vault`), a short name for
  toasts ("Plank Shelf", "Glass Case", "Pedestal", "Legendary Vault") and a sticker ("WOBBLY", "LIT", "SPOTLIGHT", "EPIC+").

**Balance** (`python3 run.py`, 8 seeds, medians; before → after). The sim adds the four pieces with their prices and base Rep,
and an upper bound for Showcase Rep: every fancy slot holds the best rarity the top unlocked district commonly gives (tier 2 Rare,
3 Epic, 4 Legendary). The "display fan" column (`mdd/displays/dmulti.py` with `progress.DISPLAY_FAN`) is a bound for players who
buy each piece the moment it costs ≤ 1 day's income.

| Measure (typical unless noted) | Before | After | Display fan bound |
|---|---|---|---|
| District 2 / 3 / top (day) | 4.8 / 12.0 / 20.5 | 5.0 / 11.8 / 20.2 | 4.9 / 11.6 / 19.9 |
| Rank 12 / Key to the City (day) | 30 / 30 | 31 / 31 | 30.5 / 32 |
| Full tree, typical | 40.5 d (13.5 h) | 39.8 d (13.2 h) | 42.6 d (14.2 h) |
| Full tree, casual / skilled | 21.8 h / 11.4 h | 23.9 h / 11.9 h | 22.8 h / 11.8 h |
| Purchase gaps before / after day 10 | 0.87 / 1.20 d | 0.94 / 1.15 d | 0.92 / 1.12 d |
| Starter treasure share | 31% | 31% | 31% |
| When the pieces are bought (typical, seed 1) | | plank day 5; glass case 32.75, pedestal 34.75, vault 35.5 | glass case ≈ 13, pedestal ≈ 20, vault ≈ 28 |
| Display Rep on day 20 / day 30 | 0 / 0 | 0 / 0 | 42 / 171 |

The plank costs a typical player about 5 minutes of income on day 5; the sim's money-first players buy the other pieces at the end,
as Golden Truck save-up filler. Typical and skilled moves of ±0.5 day are seed noise (one seed's early $150 plank shifted its
rank-10 midnight by a day). Every pacing target in §1 holds. The fan bound puts the top district at day 19.9, a tenth of a day under
the 20-day target, mostly from the *existing* trophy shelf's +40 a day from day 7, and the full tree at 14.2 h (target 12–15 h).
Casual players lose about 2 h on the full tree (≈ 24 h; §10.10 accepts "about 23 h"), because the sim's casual policy buys pieces as
soon as they help a little.

---

## 7. Late game and end goals

1. **Key to the City** (`key_to_the_city`, the main ending)
   - Requirements: **Rank 12**, plus **all 4 districts at 90% or better at midnight on 3 consecutive nights** (a `GameSession`
     counter).
   - Typical player: day 31 (10.3 h).
   - Ceremony: Mayor Bandit is revealed to be a raccoon in a suit ("explains the budget") and hands over a giant key. v1 is a
     toast, a HUD card, a Head Office letter and `GameSession.GrantKeyToCity()`.
   - It unlocks the Golden Truck.
2. **Golden Garbage Truck:** about 12.2–13.2 h for a typical player.
3. **Beat MAGNUS PRIME:** `milestone_magnus_prime_defeated`. Title "Grandmaster of Garbage", a gold chess trophy in the office, and
   Magnus sulks for a day.
4. **Trash-dex 100%** (66 valuables, rarity silhouettes, first-found day), shown on A's Career page. Title "Curator". This is the
   long tail: the Bluffs give about 1.7 Legendaries a day.
5. **Teaser: Franchise Mode (NG+).** Sell Rubbish Co. to GoldStar Premium Waste, restart at Rank 1, and keep one perk. It sits in
   the "Coming Soon™" department.

Milestone ids (for the future Feel and Shift Report work): `milestone_rank_up`, `milestone_district_unlocked`,
`milestone_first_treasure`, `milestone_first_rare`, `milestone_first_legendary`, `milestone_magnus_first_sort`,
`milestone_kevin_hired`, `milestone_city_clean`, `milestone_key_to_city`, `milestone_golden_truck`,
`milestone_magnus_prime_defeated`, `milestone_trashdex_complete`.

---

## 8. Simulated curves and balance checks

**Source:** a standard-library Python sim in
`/private/tmp/claude-501/-Users-nick-Dev-Rubbish/76c290b4-a3cc-4b9c-af3e-e0ce753baead/scratchpad/balance/`. Running
`python3 run.py` regenerates `results/RESULTS.md` and the CSVs. **That folder is a temporary scratchpad: archive it (for example to
`Docs/sim/`) before it's wiped.**

What the sim covers:
- It ports `RollBag` line by line; its output matches the audit's numbers for today's code (5.8 kg bags, about 0.35
  valuables per bag, about $200 average value).
- Each 20-minute day is planned as trips, stops and bags, hopper-limited depot returns, and a garage pile that is hand-sorted
  best-first, sorted by Magnus or crushed.
- It includes yeet-or-baby-it, Trust, Rep and ranks, Kevin, rabies and fines.
- A buying policy per player type plays a save from day 1. Results are medians over 8 seeds.

The design's original numbers, run through the same sim, gave districts at days 8.2 / 18.8 / 31.5, an 18.8 h full tree and a 53%
starter treasure share, which matches the design's own estimates and supports the model.

**Re-run 2026-10-01 for the Magnus career path (§6.3).** The sim now models it: brain L1 (the job) works from rank 2 at 8/min
with no conveyor; until he rips bags himself (lobster pincer, brain L3 or the conveyor's auto-slitter) the player spends the
rip time on every bag Magnus sorts, and he sorts at most one table-load (40, or 60 items with the XL table) per depot return
plus one; speed = rail motor ×1.25 × quad-claw ×1.4 × twin belt ×1.3; chess tier = the career formula. Prices were tried in 9
variants over 8 seeds × 3 player types; the chosen set keeps every pacing target (§1). Earlier numbers are kept in brackets.
(`catalog.py` also learned to skip `ItemCatalog`'s `Rich()` helper bodies, which broke its parser after builder D's district items.)

**Re-run 2026-10-01 for the display line (§6.8).** `data.py` gains the four display pieces (`DISPLAY_PRICES`, `DISPLAY`) and
`econ.display_rep_for` pays their base Rep plus an upper-bound Showcase Rep (every fancy slot holding the top district's best
common rarity); `progress.DISPLAY_FAN` (off in `run.py`) is the "buy every display as soon as it's cheap" bound used in §6.8.
`results/RESULTS.md` is regenerated with it; the seed-1 tables below (§8.1) are still the Magnus-career run.

### 8.1 Income by stage (typical, seed 1)

| Stage | Days | Net $/day first → last | Pickup fees + curb | Trick shots | Sorting (recycling, scrap, crush) | Valuables | Clean bonus | Kevin fees | Work Orders | Fines, wages, rabies, breakage | Valuables share |
|---|---|---|---|---|---|---|---|---|---|---|---|
| A: starter only | 1–5 | $320 → $804 | $172 | $31 | $48 | $157 | $65 | $0 | $50 | −$26 | 30% (26%) |
| B: + Meadows | 5–12 | $804 → $3,143 | $561 | $55 | $1 | $660 | $351 | $44 | $141 | −$71 | 36% (36%) |
| C: + Snootington | 13–19 | $5,098 → $16,464 | $1,937 | $9 | $12 | $4,273 | $1,757 | $264 | $667 | −$347 | 48% (50%) |
| D: + Bluffs | 19–38 | $16,464 → $29,988 | $4,488 | $0 | $736 | $16,065 | $3,940 | $589 | $2,651 | −$900 | 56% (58%) |

Valuables share by district, averaged over the days each is unlocked: Mildly Pleasant 26% (31% while it's the only district;
was 30%), Meadows 43%, Snootington 56%, Bluffs 67%. Magnus's early sorting turns the "Sorting" column positive in stages B and C.

**Today's code, for comparison:** $745 / $1,539 / $2,389 per day (casual / typical / skilled), of which 89–92% is valuables. The
$1,950 catalog is fully bought by day 2.8 (≈ 0.9 h) for a typical player.

### 8.2 Time between purchases

| Player | Days 1–10: mean / P90 / max gap | After day 10: mean / P90 / max gap |
|---|---|---|
| Casual | 0.89 / 1.25 / 3.00 d (was 0.89 / 1.25 / 3.00) | 1.08 / 2.50 / 7.00 d (was 1.21 / 2.75 / 8.75) |
| Typical | 0.87 / 1.75 / 2.50 d (was 0.86 / 1.75 / 2.75) | 1.20 / 2.50 / 7.00 d (was 1.07 / 2.00 / 7.50) |
| Skilled | 0.86 / 1.50 / 2.00 d (was 0.76 / 1.50 / 2.00) | 0.99 / 2.00 / 7.00 d (was 0.90 / 1.75 / 6.50) |

The long maximum gaps are the final Golden Truck save-up. Cosmetics, Trash-dex hunting and MAGNUS PRIME fill it.

### 8.3 No dominant strategy (net $/day, same loadout, typical player)

**Historical simulation:** this subsection uses the old compaction rates, hazard fines and landfill charges. Its crush
comparisons are not current balance claims after the 2026-10-05 compactor update. The live rates are in §3.1 and §6.2.

| Stage | Optimal mix | Crush everything | Never yeet | Yeet everything | One sweep a day | Ignore Trust/Rep | No side-loader |
|---|---|---|---|---|---|---|---|
| A (starter) | $467 | −42% | −2% | −2% | −28% (clean 40%) | ±0% | ±0% |
| B (+Meadows) | $1,594 | −38% | −1% | −16% | −38% (clean 36%) | ±0% | −14% |
| C (+Snootington) | $6,987 | −55% | ±0% | −6% | −39% (clean 30%) | ±0% | −17% |
| D (all four) | $25,405 | −53% | ±0% | ±0% | −25% (clean 56%) | ±0% | −5% |

"Ignore Trust/Rep" comes out equal because a greedy, money-only planner keeps the city clean anyway: cleanliness pays for itself.

**Hand-sort vs crush, per bag (typical player, no sorting upgrades):**

| District | Hand-sort time | Sort total | Crush (lazy $/kg − boom risk − landfill) | Sorting gain per extra second |
|---|---|---|---|---|
| Mildly Pleasant | 20 s | $7 | −$1 | $0.43/s |
| Minivan Meadows | 22 s | $28 | −$2 | $1.38/s |
| Snootington Hills | 25 s | $140 | −$4 | $6.12/s |
| Billionaire Bluffs | 27 s | $576 | −$14 | $22.40/s |

### 8.4 Does keeping the city clean pay? (typical, expected values)

| Style | Net days 1–10 | Net days 1–20 | Avg clean, days 1–20 | D2 / D3 / D4 day | Full tree |
|---|---|---|---|---|---|
| Planner (values Trust) | $9,378 | $70,936 | 87% | 5.2 / 11.8 / 19.8 | 39.5 d (13.2 h) |
| Greedy (ignores Trust and Rep) | $9,652 | $71,323 | 88% | 5.2 / 11.5 / 19.8 | 39.5 d |
| Lazy (one sweep a day) | $4,496 (−52%) | $16,792 (−76%) | 66% | 6.2 / 16.5 / 27.8 | never within 60 days |

### 8.5 Sensitivity (typical, expected values): top-district day and full tree when one knob moves ±25%

| Knob | ×0.75 | ×1.25 |
|---|---|---|
| Baseline | D4 19.8, tree 39.5 d | |
| Valuable chance | 20.5 / 41.8 | 19.0 / 37.2 |
| Pickup fees, tips, curb bonus | 20.5 / 41.2 | 18.5 / 37.2 |
| All prices and permits | 19.0 / 32.8 | 20.5 / 45.8 |
| Bags put out per day | 20.8 / 44.5 | 20.2 / 37.0 |
| Clean-streets bonus | 20.0 / 40.2 | 19.0 / 38.0 |
| Recycling $/kg | 19.5 / 39.2 | 19.2 / 39.0 |
| **Rep needed per rank** | **17.2 / 37.5** | **22.5 / 41.5** |
| Player action times | 19.5 / 38.2 | 20.8 / 41.0 |

Tuning order if playtests disagree:
1. Rank Rep thresholds (districts).
2. Late prices (the tree).
3. Pickup fees (early feel).
4. Valuable chance (treasure feel; it barely moves pacing).

### 8.6 Player time assumptions (real seconds)

| | Casual | Typical | Skilled |
|---|---|---|---|
| Share of the day spent working | 0.70 | 0.80 | 0.88 |
| Per curb stop / per bag / per sorted item | 9 / 6.0 / 2.6 | 7 / 4.7 / 2.0 | 5 / 3.5 / 1.5 |
| Extra per depot trip | 25 | 15 | 10 |
| Night work speed | 0.75 | 0.85 | 0.90 |
| Rabid bites per day on foot | 0.5 | 0.3 | 0.12 |
| Bags yeeted / trick-shot hit rate | 0.25 / 0.55 | 0.40 / 0.70 | 0.60 / 0.85 |
| Treasure bags recognised by sound | 0.3 | 0.6 | 0.8 |
| Work Orders done per day | 1.2 | 2.0 | 2.7 |
| ebae price realised vs appraisal (before fee) | 0.81 | 0.97 | 0.995 |

Fixed costs: garage out 16 s, in 10 s, TIP IT 10 s (3 s with the auto-tipper). Changing these assumptions by ±25% moves the top
district by about 1.5 days.

### 8.7 Model limits

- Solo only; co-op isn't modelled (see §9.9).
- Each district is driven separately; mixed-district trips aren't modelled, which undervalues the hopper.
- ebae price drops from selling the same item type repeatedly (audit M8) aren't modelled.
- Not simulated: gnomes, cosmetics, Phase 2 critters and fines, return-to-owner, Lost & Found, and hopper overfill.
- The Magnus rate requires the arm's `SpeedMultiplier` to scale with the contract (×0.6 to ×15.6, §6.3).
- Magnus's grip limit (5 kg tongs before the pincer) isn't modelled; few non-valuable items weigh over 5 kg.
- The career's experiential pull (a visible "next step") isn't modelled: the sim's players buy Magnus steps by payback, so steps 2–3
  land later than their rank gates (day 8.75 and 12.75 for typical seed 1). Real players will likely buy them sooner; that speeds
  the early game slightly (a cheap-early-steps variant put the top district at day 19.75 and the full tree at 12.2 h).
- Trick shots and bag tells come from the viral slice. Without them, income is about 5–7% lower and the top district slips by
  about half a day.

---

## 9. BUILD CONTRACT

### 9.1 Principles

- **Additive only.** Frozen signatures stay (`CoreLoop.md` §3, `LivingWorld.md` §1–§4). Old saves load: every new save field has
  a safe default, and saved upgrade levels keep their meaning.
- **One call for money, Rep and unlocks** (`Multiplayer.md` rule 1): new code uses `Ledger.*`, never `GameSession.AddCash`
  directly. When a builder touches an existing `AddCash` call site in a file they own, they migrate it.
- **Decide on the host, present everywhere** (rules 2, 5): gameplay rolls use `System.Random(seed)` or
  `PickupVisuals.StableHash(bagId + salt)`, never `UnityEngine.Random`. Detection rolls (vision, wand, UV) are deterministic per
  bag. No `Time.timeScale` for juice (rule 4). No new `FindFirstObjectByType` for the truck, route or station: components live on
  the truck, or receive a reference (rule 3).
- **Progression is company-scoped** (rule 11). `GameSession` is "the company" for now. Prices never scale with player count.
- **Cross-builder calls go only through Step 0 types:** `GameSession` and `Economy` additions, `Ledger`, `ProgressionEvents`,
  `Districts`/`DistrictDef`/`LootProfile`, `UpgradeEffects`, `RepIds`/`LedgerIds`, `ShopIds`. So no builder waits for another's code.
- **Facts, not rules, cross boundaries.** D and C raise facts ("a bag was collected on time", "an item was sorted correctly"). A
  turns facts into Rep, Work Orders, Trust and ranks.
- Global namespace, Unity 6 APIs, legacy `Input` is fine. Before reporting done, grep your diff for `GameSession.AddCash`,
  `UnityEngine.Random`, `Guid.NewGuid`, `NewUid(`, `FindFirstObjectByType`, `.Current`, `Time.timeScale` and
  `PlayerController.Local`, and justify or fix every new hit (`Multiplayer.md` rule 14).

### 9.2 Step 0: the contract (builder A, before anyone else compiles against it)

Builder A writes exactly these public members, with working (not stub) bodies where marked, recompiles, confirms zero errors, and
tells the others. Target: about 1 hour. Re-read `GameSession.cs` immediately before editing: it was changed at 17:37 today
(`IsGuest`, from the Multiplayer M0 work). Keep everything that is there.

**`Core/GameSession.cs` (lead file; these members only):**
```csharp
// ---------- progression (Docs/Progression.md §9.2) ----------
public static float Rep { get; private set; }                 // lifetime Business Rep (can dip; rank never drops)
public static int CleanDays { get; private set; }             // midnights that counted as Clean Days (§4.2)
public static bool KeyToCity { get; private set; }
public static float TotalCashEarned { get; private set; }     // sum of positive AddCash (increment inside AddCash)
public static event Action<float, string, string, Vector3?> RepChanged;   // (delta, repId, districtId, worldPos)
public static void AddRep(float delta, string repId, string districtId = null, Vector3? worldPos = null); // raw: Ledger.Rep applies multipliers
public static void AddCleanDay();
public static void GrantKeyToCity();
public static float GetDistrictTrust(string districtId, float fallback);  // 0..1; fallback when never set
public static void SetDistrictTrust(string districtId, float trust01);
public static int GetCounter(string key);                     // generic persisted ints (streaks, WO progress, highest rank, dex)
public static void SetCounter(string key, int value);
public static double GetStamp(string key);                    // persisted GameClock.TotalHours stamps (gnomes); -1 if unset
public static void SetStamp(string key, double totalHours);
public static readonly List<BagContents> DepotDeliveries = new List<BagContents>(); // Kevin's bags awaiting the tipping floor
public static void AddDepotDelivery(BagContents bag);
public static BagContents TakeDepotDelivery();                // null when empty
public static float InfectionResist01;                         // set by Shop (rabies_vaccine); not saved (re-applied)
public static event Action InfectionResisted;
// Infect(atHours): before infecting, if InfectionResist01 > 0 and a roll from
// new System.Random(unchecked((int)(atHours * 1000))) is < InfectionResist01 -> fire InfectionResisted and return.
```
- `SaveData` gains: `saveVersion = 2`, `rep`, `cleanDays`, `keyToCity`, `totalCashEarned`, `trustIds`/`trustValues`,
  `counterIds`/`counterValues`, `stampIds`/`stampValues` (a `List<double>`), and `depotDeliveries`.
- `Load` tolerates missing fields; `ResetAll` clears them; `ResetStatics` nulls `RepChanged` and `InfectionResisted`.
- Setters call `NotifyChanged()` and `SaveSoon()`.

**`Core/RubbishTypes.cs` → `Economy`:** the §3.1 constant changes, the new constants, and the two overloads.

**`Progression/ProgressionIds.cs`:**
```csharp
public static class RepIds { public const string BagOnTime="bag_ontime", BagLate="bag_late", CurbClean="curb_clean",
  ItemSorted="item_sorted", Contamination="contamination", TreasureUncommon="treasure_uncommon", TreasureRare="treasure_rare",
  TreasureEpic="treasure_epic", TreasureLegendary="treasure_legendary", CleanStreets="clean_streets",
  NeglectedCurb="neglected_curb", Shift="shift", WorkOrder="work_order", ChessWin="chess_win", TrophyShelf="trophy_shelf"; }
public static class RepValues { /* the §4.1 numbers as consts */ }
public static class LedgerIds { public const string PickupFee="pickup_fee", CurbBonus="curb_bonus", Recycle="recycle",
  Hazard="hazard", Scrap="scrap", LazyCrush="lazy_crush", Contamination="contamination", HazardFine="hazard_fine",
  CleanStreets="clean_streets", NeglectFine="neglect_fine", Landfill="landfill", KevinFees="kevin_fees",
  KevinWage="kevin_wage", WorkOrder="work_order", Ebae="ebae", Pawn="pawn", Hospital="hospital", Tow="tow", Style="style",
  Purchase="purchase"; }
```

**`Progression/Ledger.cs`** (working bodies):
```csharp
public struct LedgerEntry { public string kind; /* credit|charge|spend|rep */ public string id; public float amount;
  public string reason; public string districtId; public Vector3? pos; public double atHours; }
public static class Ledger
{
    public static event Action<LedgerEntry> Posted;
    public static void Credit(float amount, string reason, string id, Vector3? worldPos = null, string districtId = null); // -> GameSession.AddCash(+)
    public static void Charge(float amount, string reason, string id, Vector3? worldPos = null, string districtId = null); // fines, wages: AddCash(-amount), may go < 0
    public static bool Spend(float amount, string reason, string id = LedgerIds.Purchase);  // purchases: GameSession.TrySpend; excluded from "net"
    public static float Rep(string repId, float baseRep, string districtId = null, Vector3? worldPos = null); // × Districts multiplier; returns applied
    public static float RepTop(string repId, float baseRep);   // × TOP district multiplier
}
```

**`Progression/ProgressionEvents.cs`** (working bodies: `Raise*` just invokes). These are the only cross-builder events.
```csharp
public struct BagCollectedFact { public string bagId, stopId, districtId; public double ageHours; public bool onTime, byArm, byKevin;
  public float pay; public Vector3 pos; }
public struct ItemSortedFact { public ItemInstance item; public ItemCategory bin; public bool correct, byMagnus; public string districtId; public Vector3 pos; }
public sealed class MidnightReport { public int day; public int bagsIntoTruckToday;
  public readonly Dictionary<string, float> clean01 = new(); public readonly Dictionary<string, int> neglectedCurbs = new();
  public readonly Dictionary<string, float> cleanBonusPaid = new(); public float finesPaid; }
public static class ProgressionEvents
{
    public static event Action<BagCollectedFact> BagCollected;        public static void RaiseBagCollected(BagCollectedFact f);   // D (CurbTrash, KevinCrew)
    public static event Action<string, string, Vector3> CurbCleared;  public static void RaiseCurbCleared(string stopId, string districtId, Vector3 pos); // D
    public static event Action<ItemSortedFact> ItemSorted;            public static void RaiseItemSorted(ItemSortedFact f);       // C (SortBin, Compactor, magnet, Magnus)
    public static event Action<ItemInstance, bool> TreasureKept;      public static void RaiseTreasureKept(ItemInstance item, bool byMagnus); // C
    public static event Action<int, float, int> LoadCrushed;          public static void RaiseLoadCrushed(int bags, float kg, int treasuresLost); // C
    public static event Action<MidnightReport> Midnight;              public static void RaiseMidnight(MidnightReport r);        // D (CityCleanliness), once per rollover
    public static event Action<string> DistrictEntered;               public static void RaiseDistrictEntered(string districtId); // D (gate)
    public static event Action<bool, int, int> ChessGameEnded;        public static void RaiseChessGameEnded(bool humanWon, int plies, int tier); // C
    public static event Action<int, int> RankChanged;                 public static void RaiseRankChanged(int oldRank, int newRank); // A
    public static event Action<string> DistrictUnlocked;              public static void RaiseDistrictUnlocked(string districtId); // A (permit bought)
}
```

**`Progression/Districts.cs`** (working bodies plus the full §5.1/§5.4 table). **After Step 0 this file belongs to builder D.**
```csharp
[Serializable] public class LootProfile { public string id; public int tier; public float p0, p1;
  public float[] rarity0, rarity1;        // U,R,E,L at w=0 and w=1
  public int minItems, maxItems; public float[] categoryWeights; // J,P,Pl,M,G,H; null = today's
  public float[] tierBoost;               // index 1..4
  public float springFlat; public float valuableMult = 1f; }
[Serializable] public class DistrictDef { public string id, name, stopPrefix, permitId; public int tier, rankRequired, houses;
  public float permitPrice, bagsPerDay; public int curbCap; public float fee, tip, curbBonus, onTimeHours, lateFloorHours,
  latePayFloor, neglectedHours, cleanStreetsMax, dirtForZero, neglectFine, landfillPerBag, repMult, trustStart, critterMult;
  public bool butlerDrops; public LootProfile loot; public Rect boundsXZ; /* x=x0, y=z0, width, height */ }
public static class Districts
{
    public const string MildlyPleasant = "mildly_pleasant", MinivanMeadows = "minivan_meadows",
                        SnootingtonHills = "snootington_hills", BillionaireBluffs = "billionaire_bluffs";
    public static IReadOnlyList<DistrictDef> All { get; }   // ordered by tier
    public static DistrictDef Starter { get; }
    public static DistrictDef Get(string id);                // null-safe -> Starter
    public static DistrictDef ForStopId(string stopId);      // "mm-"/"sh-"/"bb-" prefix, else Starter
    public static DistrictDef ForBagId(string bagId);        // stopId = text before the first '/'
    public static DistrictDef ForAddress(string address);    // CurbTrashSpawner house registry, else Starter
    public static DistrictDef AtPosition(Vector3 world);     // boundsXZ, else Starter
    public static bool IsUnlocked(DistrictDef d);            // tier 1, or GameSession.GetUpgradeLevel(d.permitId) > 0
    public static IReadOnlyList<DistrictDef> Unlocked { get; }
    public static DistrictDef Top { get; }                   // highest-tier unlocked
    public static float PayFraction(DistrictDef d, double ageHours);   // Economy overload with d's windows
    public static float PickupPay(DistrictDef d, double ageHours);     // fee*frac + (on time ? tip : 0)
    public static bool IsLate(DistrictDef d, double ageHours); public static bool IsNeglected(DistrictDef d, double ageHours);
}
```

**`Progression/UpgradeEffects.cs`** (working bodies: every number in §6 as a pure function of `GameSession` levels). **A owns it
for good.** B, C and D only read it; they ask A (via their report) if a number looks wrong.
```csharp
public static class UpgradeEffects
{
    public static int Level(string id);
    public static readonly float[] CapacityKg = { 160, 200, 240, 280, 340 };
    public struct EngineTier { public float motorTorque, maxSpeed, steerAtMaxSpeed, brakeTorque, maxLateralAccel; }
    public static EngineTier Engine { get; }            // from truck_turbo (+ golden_truck clamp to 18 m/s)
    public struct HandlingTier { public float sidewaysStiffness, forwardStiffness, cogOffset, unflipHoldSeconds, antiTipBelow; public bool freeUnflip, drift; }
    public static HandlingTier Handling { get; }
    public struct ArmTier { public bool present, auto, bothSides, litterVac; public float reach, maxSpeed, deploySeconds, secondsPerBag, fumbleChance, vacRadius; }
    public static ArmTier Arm { get; }
    public static float HornScareRadius { get; }        // 12 / 25 / 40
    public static float HeadlightRangeMult { get; } public static float BeamScareRadius { get; } // 0 / 20 / 30
    public static bool AutoTipper { get; } public static bool DirectFeed { get; } public static float TipSeconds { get; }
    public static int Conveyor { get; } public static float ItemTimeMult { get; }
    public static int TableMaxItems { get; } public static bool MagnetStrip { get; }
    public static int Compactor { get; } public static float ScrapMult { get; } public static float LazyPerKg { get; }
    public static bool HazardSafe { get; } public static float GrateKeep { get; }            // 0 or 0.30
    public static float RecycleMult { get; } public static float HazardPay { get; }         // 0.75 / 1.50 / 4.00
    public static int TrophySlots { get; } public static float TrophyRepPerDay { get; }   // + DisplayPlanner.ExtraRepPerDay() (§6.8)
    // displays (§6.8): PlankShelfSlots 3, TrophyShelfSlots 6, MuseumWingSlots 18, GlassCaseSlots 4, PedestalSlots 1, VaultSlots 6;
    // PlankShelfRep 0, GlassCaseRep 10, PedestalRep 25, VaultRep 40; PedestalShowcaseMult 2; VaultMinRarity Epic
    public static float ShowcaseRep(Rarity r);          // Rare 2, Epic 4, Legendary 8
    public struct BrainTier { public float itemsPerMin, accuracy, startHour, endHour; public bool recyclablesAndJunk, valuables, hazards, ripsBags; }
    public static BrainTier Brain { get; } public static float MagnusSpeedMult { get; } public static float GripperMaxKg { get; }
    public static BrainTier BrainAt(int level);         // career text
    public static bool MagnusEmployed { get; }          // brain >= 1: the job (rail branch + tongs + brain), §6.3
    public static bool MagnusHasRailBranch { get; } public static bool MagnusRipsBags { get; }   // pincer (gripper >= 1) or brain L3
    public static float VisionDetect { get; }           // 0 / 0.6 / 1.0
    public static int MagnusChessTier { get; }          // career steps: rail + gripper + vision + brain + 1 (job) + 1 (grandmaster)
    public static int ChessTierFor(int rail, int gripper, int vision, int brain);
    public struct ChessTier { public float thinkSeconds; public int maxDepth; /* 0 = uncapped */ public float blunder; public string label; }
    public static ChessTier Chess(int tier);
    public static float CarrySlowdownMult { get; } public static int BackSackSlots { get; } public static float MaxThrowSpeed { get; }
    public static float MoveSpeedMult { get; } public static float UvDetect { get; } public static float WandDetect { get; }
    public static float InfectionResist { get; } public static float BonkKnockbackMult { get; } public static float KeepBagOnBonk { get; }
    public static float EbaeFee { get; } public static int EbaeExtraBuyers { get; } public static float EbaeOfferSpeed { get; } public static float EbaeAutoAccept { get; } // 0 = off
    public static float KevinWage { get; } public static float KevinCoverage(int districtTier);
    public static float ValuableChanceMult(int districtTier);  // gnomes (reads stamps) × Rookie Luck is D's, not here
    public static bool DetectRoll(string bagId, string salt, float chance); // (StableHash(bagId + salt) & 0xFFFF) / 65535f < chance
}
```

**`Shop/ScrapazonCatalog.cs` (A):** add a `ShopIds` constant for **every** id in §6 (including `key_to_the_city` as a pseudo-id),
and the additive `ScrapazonItem` fields: `string dept`, `int[] rankRequired`, `string[][] prereqs`, `string[] levelTitles`,
`string[] levelBlurbs`, `string blurb`, `float dailyWage`. The item data itself can land after Step 0.

**`Progression/Progression.cs`:** Step 0 only needs these compile-safe properties (A fills in the logic afterwards):
`Rank`, `Next`, `HasRank(int)`, `RankTitle(int)`, `CanBuy(ScrapazonItem, int level, out string whyNot)`, `IsSubscribed(string stopId)`,
`Subscribers(DistrictDef)`.

### 9.3 Who raises which fact, and what A does with it

| Fact | Raised by | A turns it into |
|---|---|---|
| `BagCollected` | D: `CurbTrash.CollectIntoTruck` and `KevinCrew` | `bag_ontime`/`bag_late` Rep (half for Kevin), WO progress, today's stats |
| `CurbCleared` | D: `CurbTrash` (last bag at a house) | `curb_clean` Rep, WO |
| `ItemSorted` | C: `SortBin` correct/wrong, compactor junk, magnet strip, Magnus. Resolves `districtId` via `Districts.ForAddress(item.foundAt)`. | `item_sorted` / `contamination` Rep, WO |
| `TreasureKept` | C: keep crate (hand or Magnus), grate rescue | `treasure_*` Rep, Trash-dex, WO, `milestone_first_*` |
| `LoadCrushed` | C: CRUSH ALL | WO `wo_no_crush`, stats |
| `Midnight` | D: `CityCleanliness.MidnightInspection()` | Trust ±, `clean_streets` / `neglected_curb` / `shift` / `trophy_shelf` Rep, Clean Day, Key to the City streak, new Work Orders, yesterday's stats, promotion check |
| `ChessGameEnded` | C: `MagnusChessBot` | `chess_win` Rep (once a day), WO, MAGNUS PRIME milestone |
| `DistrictEntered` | D: gate ribbon | first-visit toast, stats |
| `EbaeMarket.ItemSold` (existing) | Ebae | WO `wo_ebae_fair` |

Cash stays with the builder who owns the rule: D pays fees, curb bonuses, clean-streets, fines and Kevin; C pays recycling,
hazard disposal and compactor scrap. Both go through `Ledger`; compaction no longer charges landfill or hazard fees.

**Midnight order** (host):
1. D's spawner detects the rollover and calls `CityCleanliness.MidnightInspection()`. That pays each district's clean-streets
   bonus (formula §3.3) and neglect fines, and updates D's clean streak counters (`clean_streak_{id}`).
2. D's `KevinCrew` charges the wage.
3. D raises `Midnight(report)`.
4. A applies Rep, Trust, Clean Day, Key to the City, Work Orders and the promotion check.

### 9.4 File ownership for this build

Only edit files you own. Create new files only in your folders. `Editor/` = `Assets/Scripts/Editor/`.

| Builder | Owns / may create |
|---|---|
| **A: progression core + Scrapazon trees UI** | Step 0 files above: `Core/GameSession.cs` and `Core/RubbishTypes.cs` (**only** the listed members); `Assets/Scripts/Progression/**` (except `Districts.cs` after Step 0); `Shop/ScrapazonCatalog.cs`, `Shop/ScrapazonStore.cs`, `Shop/ScrapazonApp.cs`, `Shop/ScrapazonArt.cs`, `Shop/ScrapazonBox.cs`, `Assets/UI/shop/**`; `Ebae/EbaeMarket.cs` (additive Power Seller hooks only: fee, extra buyer, offer speed, auto-accept, the vision +3%); `Editor/Setup_Progression.cs` (debug menu only: grant Rep, set rank, unlock district, add cash, skip to midnight, grant Clean Days) |
| **B: truck + gear** | `Shop/ShopEffects.cs`, `Shop/ShopEffectsRunner.cs`, `Shop/ShopTruckMods.cs`, `Shop/ShopHeadlamp.cs`, new `Shop/Truck/**` (`TruckSideLoader`, `TruckHandlingMods`, `TruckRecovery`, `GoldenTruckFx`), new `Shop/Gear/**` (`BackSack`, `BootsMods`, `TreasureWand`, `UvLamp`, `GarbageJuice`); **additive exceptions:** `TruckControllerV2.cs` (only a public `maxLateralAccel` default `float.PositiveInfinity`, the cornering clamp `δ = h·min(range, atan(L·aLatMax/v²))` with L = 2.64, and `WheelCollider.ConfigureVehicleSubsteps(5, 12, 15)`, with no behaviour change at default), and `Critters/CritterKnockback.cs` (only in `HitLocalPlayer`: multiply the distance by `UpgradeEffects.BonkKnockbackMult`, and skip `carrier.Drop()` when a roll < `UpgradeEffects.KeepBagOnBonk`) |
| **C: garage + Magnus** | `Assets/Scripts/Sorting/**`, `Editor/Setup_SortingStation.cs`, new `Assets/Scripts/Garage/**` (`AutoTipper`, `ConveyorLine`, `ConveyorFeeder`, `ConveyorTray`, `MagnusSorter`, `TrophyShelf`, `DepotDeliveryDropper`, `GarageUpgradeVisuals`), `MagnusArm.cs`, `MagnusTrack.cs`, `MagnusChessBot.cs` (strength, blunder, banter, `ChessGameEnded`), `Core/Search/Searcher.cs` (only the `MaxDepth` bound), new `Editor/Setup_GarageUpgrades.cs` |
| **D: districts + loot** | `Editor/NeighborhoodBuilder.cs`, `Editor/Setup_Pickup.cs`, `Editor/Setup_Items.cs`, `Assets/Scripts/Trash/**` (incl. new `KevinCrew.cs`), `Assets/Scripts/Items/**`, new `World/DistrictGate.cs` + `World/DistrictWelcome.cs`, `Progression/Districts.cs` (after Step 0) |
| **Nobody in this build** | `Character/**` and `Editor/Setup_Character.cs` (Chuck Dumpling is landing), `Critters/**` and `Status/**` (except B's two lines), `Feel/**` (use `FeelApi`; A builds its own HUD overlay with the public `FeelUi` helpers), `PlayerController.cs`, `Core/TrashCarrier.cs`, `Flow` files except B's exception, `EbaeUI.cs`, `LaptopController.cs`, `Core/GameClock.cs`, `Core/InteractionZone.cs`, `Core/RubbishAssets.cs`, `Core/Carry.cs`, `Docs/**` |

**Keep existing public members.** `ScrapazonApp` (A) reads `ShopEffects.AirHornScareRadius`, `BaseHornScareRadius` and
`TruckCapacityFor`, so B keeps them and backs them with `UpgradeEffects`. The first-person sort station has landed, which is why
`Sorting/**` is open to C again.

**Files being edited right now by other workstreams:** `TruckControllerV2.cs`, `Core/TrashCarrier.cs`,
`Critters/CritterKnockback.cs`, `Shop/ShopHeadlamp.cs`, `Status/RabiesStatus.cs` and `Core/GameSession.cs` all changed after
17:26 today (the Chuck integration and Multiplayer M0). **Re-read any shared file immediately before each edit, keep edits
minimal, and never revert changes you didn't make.**

### 9.5 Builder scopes, in build order

**A: progression core + Scrapazon trees UI**
1. Step 0 (§9.2).
2. `Progression.cs`: the rank table (§4.3); a monotonic rank (counter `rank_reached`); fact → Rep (§9.3); Clean Days; midnight
   handling; Trust updates (§5.2); `Subscribers`/`IsSubscribed`; Key to the City; Trash-dex counters (`dex_<defId>` = day found);
   `DailyStats` for today and yesterday (bags, on-time %, curbs, items, contamination, treasures, best find, credits, charges, Rep).
3. The catalog: every item and level in §6 with titles, blurbs, prices, ranks, prereqs, depts and reviews. `ScrapazonStore.TryBuy`
   gains `ScrapazonPurchase.Locked` with messages like "Requires Rank 6: Hopper Whisperer", "Requires Grabby Arm Lv 1", "Requires
   the Key to the City". Purchases go through `Ledger.Spend`. Permits raise `DistrictUnlocked`.
4. The Scrapazon UI:
   - departments: All, Truck, Garage, Magnus, Gear, Critters, Pharmacy, Business, Permits, Cosmetics, **Career**, Coming Soon™;
   - cards show "Lv 2/3", the next level's title and blurb, a LOCKED chip (rank or prereq), and the Kevin wage;
   - a "New for you" ribbon after a promotion, and a "Best next buy" shelf;
   - the Magnus department opens with his **career strip** (§6.3: step N of 9, step tiles, "Magnus can now", "Next" with the exact
     lock reason) and lists his cards in career order;
   - the existing capacity text uses `UpgradeEffects.CapacityKg`.
5. The Career page: the rank ladder, a Rep bar, Clean Days, the next rank's unlocks, per-district rows (locked or unlocked, Trust,
   Subscribers n/N, clean %), today's 3 Work Orders, the Key to the City checklist, Magnus's chess tier, and the Trash-dex grid.
6. The HUD chip (own overlay canvas under FeelHUD): "RANK 5 · Senior Sack Specialist" plus a thin Rep bar. A "+25 REP" floater
   only for events of 10 Rep or more. The promotion stamp card (§4.4).
7. Work Orders (§4.5).
8. The ebae Power Seller hooks.
9. The Golden Truck gate. The Head Office letters (promotions, dirty districts, the Kevin nudge).
10. The debug menu.

**B: truck + gear**
1. `ShopTruckMods` applies `UpgradeEffects.Engine`, including stock brake 1,000, and the capacity L4 ram visual.
2. `TruckHandlingMods` (friction curves via copied `WheelFrictionCurve` structs, the CoG offset, anti-tip, drift) and
   `TruckRecovery` (Hold R, tow fee, free with handling L1).
3. The `TruckControllerV2` additive clamp, for turbo L3.
4. `TruckSideLoader` L1–L3: attached by `ShopEffectsRunner.ScanTrucks`; renderer-only arm, with the rules in §6.1.
5. Horn L2 radius (`ShopEffects.HornScareRadius` → `UpgradeEffects`); headlights L1/L2.
6. Gear: gloves via public `TrashCarrier` fields and the Back Sack; boots via `ThirdPersonController` speeds; the UV lamp; the
   wand (on `TrashBag.AnyPickedUp`); the vaccine (`GameSession.InfectionResist01 = UpgradeEffects.InfectionResist`, re-applied on
   spawn); the hard hat (the CritterKnockback lines); garbage juice.
7. Golden Truck visuals and speed. Paints, horn pack and radio (stretch).
8. `ShopEffects.OnPurchased` gets a default branch ("{name} installed!" + `ScrapazonBox.Deliver`) for new ids.

**C: garage + Magnus**
1. Chess strength (tiers, `Searcher.MaxDepth`, blunder, banter, `ChessGameEnded`).
2. Economy hooks in Sorting:
   - recycling ×`RecycleMult`, hazard pay, the shared compact $/kg and fee-free bulk hazard effects;
     the legacy district `landfillPerBag` data is not charged by compaction;
   - migrate the touched `AddCash` calls to `Ledger`;
   - raise `ItemSorted`, `TreasureKept` and `LoadCrushed`;
   - no combo for `auto` sorts.
3. Compactor Mk II L1–L3; sorting table L1–L2 (60 items, magnet strip).
4. Auto-tipper L1 (and L2 after the conveyor).
5. The conveyor: Bag Belt (L1), auto-slitter (L2), twin lanes (L3). `SortStation.NextBag` prefers the tray, then vision-flagged
   bags. `SceneTrashKeeper` handles belt bags.
6. `Setup_GarageUpgrades`: everything built from primitives under one root `GarageUpgrades` in ShopScene, **always present but
   shown by upgrade level** (no runtime scene edits). It covers the rail branch visual, the Sort Stop transform, the belt path, the
   shelf and the disco ball.
7. `MagnusArm` additions: `SpeedMultiplier`, an `IdlePose.Work` with a `workFocus`, `Step.onGrab`, `CanReach(Vector3)`.
   `MagnusTrack` gets a sort stop. Then `MagnusSorter` (§6.3). Upgrade visuals.
8. Magnus vision flags on pile bags (`Fx.Sparkle` + "!", deterministic).
9. The trophy shelf (inventory props; A pays its Rep). `DepotDeliveryDropper` (Kevin's bags onto the pile; cap 40).

**D: districts + loot**
1. Items:
   - the §3.2 retune;
   - the `RollBag(bagId, seed, wealth, foundAt, LootProfile profile, float trust01 = 1f)` overload with the §5.4 rules and the §5.5
     rich themes;
   - item tier metadata (`ItemCatalog.TierOf(def)`);
   - 31 valuables and 21 non-valuables; `fragile` tags; Rookie Luck;
   - gnome multipliers via `UpgradeEffects.ValuableChanceMult`;
   - new `ItemVisuals` keys, Legendaries and Epics first.
2. Trash:
   - a `districtId` field on `PickupStop`, `HouseDirectory.Entry` and `CurbTrashSpawner.House` (plus `stopId` prefixes);
   - per-district spawn rates, subscribers, caps and Butler Drops, skipping locked districts;
   - district pay, lateness, freshness and neglect via `Districts.*`;
   - per-district `CityCleanliness` with `MidnightInspection()` (replacing `PayCleanStreetsBonus`) and the §3.3 formulas;
   - `TrashRoute` works across unlocked districts;
   - raise `BagCollected` and `CurbCleared`; migrate the touched `AddCash` calls to `Ledger`.
3. `KevinCrew` (§6.5).
4. The NeighborhoodBuilder refactor into per-district specs; district 1 byte-stable. Then Minivan Mile + Meadows + gate + ribbon.
   Then Snootington. Then the Bluffs.
5. `Setup_Pickup.WriteHouseDirectory` writes `districtId`. The expanded PlayBounds and per-district perimeter walls.

### 9.6 Setup scripts and the order to run them

Code first, in parallel, then scene and asset setup **one at a time** by whoever integrates:

1. **Step 0 (A)** → recompile, then `unity command console_status --json` (`groundTruth.compilationFailed` must be false).
2. **A, B, C and D code in parallel.** Each builder runs `recompile` → `recompile_status` → `console --level error --tail 50
   --json`, filtered to their own files. If someone else's error blocks the assembly, wait and retry. **No play mode, no scene saves
   during this phase.**
3. **Setup runs** (idempotent `Setup_<X>.Run()`, menu `Rubbish/Setup/*`), in this order:
   **D `Setup_Items` → D `Setup_Pickup` → C `Setup_GarageUpgrades` (ShopScene) → `Setup_Displays` (ShopScene, §6.8) → D Neighborhood rebuild
   (`NeighborhoodBuilder.Build`, which also writes `HouseDirectory`)**. B and A have no scene setup; truck and gear visuals are
   built at runtime like `ShopTruckMods`.
4. **Editor rules for step 3 and for any play-mode test:**
   - `unity command editor_status` must show `playMode == "stopped"`. Never stop the user's session: poll and wait.
   - Take the lock: `mkdir /private/tmp/claude-501/-Users-nick-Dev-Rubbish/76c290b4-a3cc-4b9c-af3e-e0ce753baead/scratchpad/editor.lock`.
     If it exists and is under 30 minutes old, wait. Remove it when done.
   - Open scenes **additively**, save only the scene you changed, close it if you opened it. **Never save Bootstrap** (its open
     unsaved changes may be the user's).
   - Screenshots: `shot.sh <name>`, then delete `Assets/Temp` and `Assets/Temp.meta`.
5. **Integration test** (with the lock): `editor_play` from Bootstrap, `host.cs` then `host2.cs` (local host into ShopScene, no
   Relay). Then the debug menu: set rank 4, buy the permit, drive the truck through the Meadows Gate (ribbon), collect a Meadows bag
   (+$15), skip to midnight (per-district bonus, Trust change, Clean Day). Open Scrapazon (locks, Career page, the Magnus
   department's career strip). Magnus: buy career step 1 (brain L1) at rank 2, rip a bag onto the table, and he commutes and sorts
   it; buy step 3 (gripper L1) and he yoinks pile bags himself. `editor_stop`, release the lock.

### 9.7 Acceptance checks (each builder, via `eval_file`, reported with numbers)

- **A:**
  - Debug Rep crosses each threshold and ranks change once, never down. With Clean Days below the minimum, the promotion waits for
    the next midnight.
  - `TryBuy` returns `Locked` with the right message for rank, prereq and Key to the City.
  - A save round-trip keeps every new field. A v1 save (without them) loads with defaults and its upgrade levels intact.
  - 3 Work Orders roll the same way for the same day. Ledger: purchases are excluded from "net".
- **B:**
  - Truck fields match `UpgradeEffects.Engine` for turbo levels 0–3.
  - Arm L1: in play mode at a curb, a bag goes truck-ward, the curb record count drops by 1, and cash rises by exactly the district
    fee.
  - Recovery rights a flipped truck.
  - `InfectionResist01 = 1` means `Infect` doesn't infect.
- **C:**
  - Chess: `MaxDepth` bound is respected, blunder rate ≈ table ±5% over 200 moves (edit mode, engine only).
  - The auto-tipper pulls the lever. A belt bag reaches the tray.
  - Magnus sorts 10 items into correct bins with no combo change, and his measured items/min is within ±10% of the contract.
  - Magnus career (edit mode, `MagnusCareer`): the catalog prereq graph is acyclic and every level reachable; the 9 steps in order
    are each buyable once the earlier steps are owned, with non-decreasing rank gates; the first Magnus item is the job (rank 2,
    no conveyor prereq) and `MagnusSorter.ShouldStartShift` passes every gate before "nothing to do" with only brain L1; every step
    raises the chess tier, and the whole path reaches tier 11.
  - Compactor Mk II: L2 no boom, L3 dented valuable in the KEEP crate.
  - Displays (§6.8, edit mode, `DisplayPlanner.Plan` with injected levels and pins): best finds go pedestal → vault (Epic+ only)
    → glass case → trophy shelf → museum wing → plank; a removed (sold) find leaves; Arrange pins swap two finds and cycle back to
    automatic; a stale pin is ignored; `TrophyRepPerDay` = 0 / 40 / 160 by trophy shelf level, plus the pieces' base and Showcase
    Rep; `Setup_Displays` leaves 6 gated pieces, 38 empty slots and no `GarageUpgrades/TrophyShelf`.
- **D:**
  - Diff `HouseDirectory` before and after: every district-1 entry (stopId, address, wealth, position) is identical.
  - New stopIds carry their prefix.
  - Monte Carlo of 10k bags per profile: valuables per bag and EV per bag within ±10% of §5.4.
  - The spawner puts nothing out in locked districts, and subscriber counts match §5.2.
  - Exactly one `Midnight` raise per rollover.
  - The gate blocks the truck when locked and opens at permit level 1.

### 9.8 Save and persistence notes

- Everything is persisted through `GameSession` (§9.2), which is the future company file (`Multiplayer.md` §2.1). No new
  per-machine JSON.
- Existing saves start at Rank 1 with Rep 0 and keep their cash and upgrades. Rank gates stop a big old bank balance from buying
  everything at once.
- Curb records, the clock and truck cargo are still reset each server session (known save gaps; producer M4). Progression data is
  not.

### 9.9 Co-op readiness (`Multiplayer.md` §9)

- Money, Rep and unlocks go through `Ledger`.
- Host-only: the spawner, Kevin, arm target choice, the auto-tipper, the belt feeder, Magnus picks, midnight.
- Deterministic hash rolls for detection. Fact events carry ids, not object references (bagId, stopId, item uid).
- **Supply scales, prices don't:** when co-op lands, bags per day = district rate × (1 + 0.4 × (online members − 1)). Rank pacing
  is held by Clean Days (§4.2).
- Permits belong to the company. A locked gate's collider exists only on the driver's machine when that company lacks the permit
  (`Multiplayer.md` §4.8). The side-loader is operated from the shotgun seat when occupied.

---

## 10. Open questions for Nick (each has a default that is already built into this spec)

1. **Original mechanics proposal:** trick-shot style pay per district, the "Green Guarantee" landfill surcharge, CRUSH ALL
   explosions, the clean-streets formula with its streak and Clean City factor, neglect fines, shift Rep and Work Orders.
   *Original default: all in. 2026-10-05 override: compaction landfill and hazard fees removed; bulk keeps comic effects.*
2. **Roll-off dumpsters cut**, and the hopper stops at 340 kg. *Default: cut.*
3. **Golden Truck at $140k**, requiring the Key to the City plus turbo L3 and arm L3. *Default: yes.*
4. **Starter treasure cut about 4×** (0.08 valuables per bag, $66 average; the wedding ring becomes Epic). Rookie Luck covers the
   first find until the onboarding seeded bag (producer M9) lands. *Default: yes.*
5. **Magnus is weak at chess by default** (tier 0 ≈ 300 "Elo", 40% blunders) and strengthens with upgrades. *Default: yes.*
6. **The Magnus sorter commutes to your sort station** (so he can't play chess while sorting, and steps aside when you sit
   down), instead of a desk with pneumatic tubes. *Default: commute.*
7. **Side-loader on X;** automatic from L2; operated from the shotgun seat in co-op. *Default: yes.*
8. **All districts in one scene;** flat first, elevation later. Meadows is reached by a new road from the depot that can become
   Depot Row. *Default: yes.*
9. **Rank needs Rep plus Clean Days** (a no-op solo; a brake for crews). *Default: yes.*
10. **Casual players are about 1.7× slower** (top district at 11.3 h, full tree about 23 h). *Default: accept, plus the "Best
    next buy" shelf and the Kevin nudge.*
11. **Existing saves** restart at Rank 1 but keep cash and upgrades. *Default: yes.*
