# Rubbish: the campaign ("Take the city back")

> **Status, 2026-10-09:** §1 to §13 are a PROPOSAL (nothing approved, not a build contract). **§14 is built:** Nick asked for a
> first slice before anything else changes, and it is in the code, compiled and unit-tested, but **not yet played**.
>
> - **(code)** = read from the working tree on 2026-10-09 by four read-only sweeps. Nothing was run in Unity.
> - **(model)** = output of [`sim/campaign_model.py`](sim/campaign_model.py), a small expected-value model. Its player
>   timings are the assumptions from `Progression.md` §8.6, which nobody has measured in play. Full output:
>   [`sim/campaign_results.md`](sim/campaign_results.md).
> - Playtest metrics (how the numbers will be checked): [`Metrics.md`](Metrics.md).
> - Everything else is a suggestion. The decisions that are Nick's are in §12.

## 1. The goal

Today the game asks you to keep a clean town clean, and fines you when you slip. This turns that around. **Every
district starts filthy, and nobody blames you for it.** You work a district, earn in it, buy the one piece of kit its
mess demands, push it past a cleanliness threshold, hold it there through a few midnight inspections, and then beat
the raccoon capo who runs the place. That wins the district's waste contract, and the contract opens the next
district. Six contracts and the Raccoon King end the campaign with the Key to the City.

Contracts replace permits. A gate opens because you won the district before it. Nothing is bought at a gate.

## 2. One district, start to finish

1. **Arrive.** The district reads about 0% clean: dumps in the parks and alleys, raccoons everywhere, GoldStar
   stickers on most bins.
2. **Work it.** Your customers' curb bags pay as they do today. Bags pulled off the dumps pay a city bounty.
3. **Hit the wall.** Part of the filth can only be removed with the district's key item, so the meter stalls
   between 50% and 65%, short of the threshold.
4. **Buy the key item** and clear the rest.
5. **Hold.** Stay at or above the threshold for a number of midnights in a row. The mob raids on those nights.
6. **Showdown.** When the hold is complete the capo can be called out, at night, when the players choose.
7. **Contract won.** The raccoons leave the district, every house becomes a customer, the district pays a nightly
   retainer, and the next gate opens.

## 3. Filth, customers and the Clean %

### 3.1 What the game does today (code)

- Clean % for a district is `1 - dirt / dirtForZero` (30 in the Acres). Dirt is each curb bag weighted by age (0.1
  fresh, up to 3.0 when neglected) plus 0.15 per loose item. A dozen stale bags take the Acres to 0%.
- It is worked out on each machine from the live curb bags and is **not saved**. Curb bags are wiped when a session
  starts and 8 or 9 fresh ones are seeded. So every session starts nearly spotless: a district cannot start filthy
  today, or stay filthy between sessions.
- Supply follows Trust. 25% + 75% x Trust of a district's houses are subscribers; the rest wear a GoldStar sticker
  and put out nothing. Trust starts at 40% (60% in the Acres) and moves +15, +5 or -10 points a night.
- A work director scales how much trash appears to your pace (0.28x to 1.38x when solo).
- Dirt is punished by: lower pay for late bags; neglect fines (waived for a business's first 48 game hours, then
  capped at $10 a night, later $25 x the top district's tier); -3 Rep per neglected curb (-6 since the 2026-10-12 pace pass); Trust -10; the Clean
  Streets bonus lost, and cut for every district by the worst one; missed Clean Days, which gate ranks 4 and up; a
  reset of the Key to the City streak.
- Raccoon numbers follow the town-wide dirt. The per-district `critterMult` is never read.
- A dirty district looks the same as a clean one, apart from the heap, stink lines and flies on a neglected curb.

### 3.2 Proposed

**Filth** is a saved number per district, owned by the Town (§9). It comes in three kinds:

| Kind | What it is in the world | Who can remove it |
|---|---|---|
| Heaps | old bags in dumps around the district. One unit = one bag pulled off a heap | anyone, by hand or with any truck |
| Locked filth | the district's own kind of mess (§5) | only that district's key item |
| Stale curbs | your customers' bags left out past their window (1.5 units a bag) | anyone |

- **Clean % = 1 - filth left / filth at the start.** Every unit hauled away moves the meter, from the first bag.
- **Customers replace Trust.** Share of houses that are your customers = 55% + 45% x the best Clean % the district
  has reached (70% + 30% in the Acres). That is today's starting share, and it only ever goes up. A house that
  signs up swaps its GoldStar sticker for yours. More customers means more income, so cleaning grows the business.
- **GoldStar's night run.** Half of what the non-customers throw out is dumped back on the heaps each night. It can
  never make a district filthier than it started, and it fades by 3% for every day you spend there (to 40% at the
  lowest) so that nobody is stuck. As customers rise, the dumping dies down by itself.
- **Cleaning pays.** A city bounty per unit removed ($2.50 in the Acres up to $20 in the Bluffs). City Hall grants
  worth 10% and 25% of the key item's price at 25% and 50% clean. A contract bonus equal to the key item's price.
  After the contract, the retainer: today's Clean Streets payment ($60 to $1,500 a midnight) scaled by Clean %,
  with no streak and no worst-district factor.
- **No punishment for dirt.** Removed: neglect fines, the Rep loss for neglected curbs, Trust falling, the
  worst-district cut, Clean Days. Kept: a late bag still pays less.
- **Won stays won.** A contract is never lost. An ignored district only earns less.
- **The work director stays**, but heap bags must not count as curb bags for it, or it throttles a filthy district
  to its floor.
- **How it should look.** Dumps (the neglected-curb heap and stink effect, larger), loose litter, raccoons
  concentrated in filthy districts (driven by the district's own Clean %), and a haze and fly ambience that fades
  as the meter rises. On contract day the district switches to a clean look. That last part is the expensive one,
  because the town dressing is baked.

## 4. Why every district needs a key item

"Impossible with the gear you arrive with" cannot come from how fast you haul. The model's ceiling for bags hauled
from a district in a day, with everything crushed and no curb round:

| District | Previous tier's gear: casual / typical / skilled | This tier's gear | Gain (typical) | Skilled on old gear / casual on new |
|---|---|---|---|---|
| Acres | 59 / 77 / 97 | 92 / 126 / 168 | x1.63 | x1.05 |
| Meadows | 72 / 94 / 120 | 103 / 130 / 155 | x1.37 | x1.16 |
| Commons | 111 / 142 / 171 | 121 / 154 / 187 | x1.09 | x1.42 |
| Hills | 96 / 120 / 143 | 139 / 166 / 187 | x1.38 | x1.03 |
| Heights | 135 / 161 / 180 | 152 / 182 / 205 | x1.13 | x1.18 |

The last column is above 1 in every row: a skilled player on the old gear out-hauls a casual player on the new gear.
Friends widen the gap again, and the work director feeds a fast crew more trash. A haul-rate threshold would be
either beatable on old gear or out of reach for a slower player on new gear.

So each district gets **locked filth**: a kind of mess only its key item can remove, sized so that the threshold is
out of reach without it. Thresholds are percentages and holds are nights, so neither depends on skill or crew size.
Coupon Commons already works this way with its dumpsters.

## 5. The six districts

Fee, bags, stops and distances are (code). Round trip is depot to the average stop and back by road. "Days there" is
(model), typical solo player. The hold counts the first night at the threshold.

| # | District | Fee + tip (before the 2026-10-12 pace pass; all x3 now), bags a day, stops | Round trip | Locked filth | Key item (today's price) | Threshold, hold | Starting filth (locked share) | Days there |
|---|---|---|---|---|---|---|---|---|
| 1 | Mildly Pleasant Acres | $4, 35, 37 | 710 m | dumped furniture, too big for the Curb Pup's hopper | Rear-loader, $600 | 70%, 1 night | 80 units (35%) | about 5 |
| 2 | Minivan Meadows | $15, 30, 22 | 1,555 m | the HOA's lidded "raccoon-proof" wheelie bins | Grabby Arm L1, $1,800 (needs Sticky Tires, $250) | 75%, 2 | 120 (40%) | about 5 |
| 3 | Coupon Commons | $25 + $5, about 47, 9 | 1,167 m | dumpsters **(built)** | Dumpster Dunker, $2,400 | 80%, 2 | 150 (45%) | about 5 |
| 4 | Snootington Hills | $40 + $15, 24, 14 | 1,784 m | bin nooks behind the 3 m hedges | Arm L2 "Crawl & Grab", $9,000 | 80%, 3 | 170 (40%) | about 6 |
| 5 | Stackington Heights | $55 + $20, 26, 8 | 1,996 m | litter rained from the tower balconies | Octo-Arm with Litter Vac, $39,000 | 85%, 3 | 190 (45%) | about 7 |
| 6 | Billionaire Bluffs | $80 + $40, 20 in two Butler Drops, 8 | 2,761 m | helipads, roof terraces, moat islands | Hoverhog, $85,000 | 90%, 3 | 160 (50%) | about 9 |

What each lock costs to build:

- **Commons:** nothing. Dumpster bags already need the Dunker.
- **Bluffs:** small. The Hoverhog's claw exists; some Butler Drops go where only it can reach.
- **Heights:** small. The Litter Vac exists; it needs the balcony litter and a saved count.
- **Meadows and Hills:** medium. A new container on a stop, built like the dumpster's extra slots, emptied by the
  arm instead of the Dunker (Hills adds a reach rule).
- **Acres:** medium. A two-hand "bulk" item, like today's heavy bag, that the Pup's hopper refuses.

## 6. The upgrade path

**Rule: rank r needs ⌈r/2⌉ - 1 contracts.** That is two ranks per district, and it replaces Clean Days. All six key
items are already in the catalog in rising price order, and only two rank gates move.

| District | Ranks | Key item | Also in that tier, by today's rank gates |
|---|---|---|---|
| Acres | 1-2 | Rear-loader | hopper +40 and +80 kg, Turbo 1, Sticky Tires, Air Horn, headlights, Bag Cannon, Magnus Gets a Job |
| Meadows | 3-4 | Grabby Arm L1 | hopper +120 kg, Auto-Tipper, Bag Belt, XL table, Compactor Mk II, recycling x1.5, Turbo 2, suspension, Magnus's rail motor and pincer, ebag badge, trophy shelf |
| Commons | 5-6 | Dumpster Dunker | hopper +180 kg, Heavy-Duty belt, compactor L2, recycling x2, Magnus's googly eyes, Foghorn, light bar, second sorting seat, glass case |
| Hills | 7-8 | Arm L2 **(moves from rank 6 to 7)** | Turbo 3, Dunker II, Treasure Sense, Appraisal Monocle, Junk Diverter, recycling x3, drift kit, ebag L2, museum wing |
| Heights | 9-10 | Octo-Arm **(moves from rank 10 to 9)** | Hazmat brain, Quad-Claw, treasure grate, ebag L3, pedestal |
| Bluffs | 11-12 | Hoverhog | its upgrade lines, Grandmaster Module, recycling x4, the Vault. Golden Truck after the Key |

Prerequisites that name a permit (the Dunker, the Hoverhog, satellite pads, the raccoon crew's districts) become
"that district is open".

**What a typical solo player buys there (model).** Spend per district: $1,500, $6,500, $14,200, $34,500, $68,000,
$224,500. The key item is bought on day 3, 2, 1, 2, 4 and 3 of its district. It costs about 1 to 1.5 days of that
district's average income for the first four and 3.5 to 4 days for the last two. The Grandmaster Module, the
Hoverhog lines and the Golden Truck are left for after the campaign.

**Prices: leave them alone for now.** In the model, changing one key item's price moves days from one district to
another but barely changes the total, because cash carries over (§8). The Dunker is bought on arrival by every
player type, which is fine: Commons barely works without it.

## 7. The raccoons

### 7.1 Lore

- **Name.** My pick is **the Ringtail Family** ("the Ringtails"), with tail rings showing rank. Others: the Bandit
  Family, the Trash Panda Syndicate, the Little Hands, Local 404 (a mobbed-up bin men's union).
- **GoldStar Premium Waste is their front.** It already exists as the sticker on non-customers' bins. The city pays
  GoldStar; GoldStar collects nothing, the Family eats what it likes and dumps the rest. That is why the town stinks.
- **Mayor Bandit** is already "a raccoon in a suit" in two Head Office letters (code). The holds are his red tape:
  his office wants more clean inspections before each contract can change hands. Suggested twist: he and the Raccoon
  King are never seen in the same room.
- **The Raccoon King** already turns up on about one night in four and leaves at dawn shouting "I SHALL RETURN!"
  (code). Keep him as the recurring nemesis who turns up more often as you take districts.
- **Already in the game, now one story:** the Fence "answers to the Raccoon King" (code); the Lucky Raccoon casino
  has human staff and no owner yet; the Raccoon kit's standing ladder (Friend, Family, Made) exists but only a debug
  menu reads it. Mr. Pemberton and the HOA Lady, who already sign complaint letters, can be the faces of the
  inspection. Chuck is "terrified of raccoons" in his character sheet.

### 7.2 Showdown rules

1. **Capos are the King with cover.** The King is the only thing in the game with hit points (code): a thrown item
   or a punch does 1, being run over does 2. A capo cannot be hit until the district's key item has done its job on
   his cover. Then he is stunned and takes hits like the King.
2. **Your kit is your style, never the key.** X-Ray Specs show which dumpster Lucky is in; a Made player's goons
   stand back; Rocket Blades win a chase. None of them is required.
3. **Hit points grow per player**, as the King's do (8 + 4 a player today).
4. **Players start it**, at night, once the hold is complete. The Big Cleanup's announce, run and results phases
   already carry a timed event with a banner and scoreboard.
5. **Losing is cheap.** He trashes about 10% of the district and you may call him out again the next night.

### 7.3 The six capos

| District | Capo | Cover: what the key item has to do | Then | Suggested hit points |
|---|---|---|---|---|
| Acres | **Louie the Lid**, in a furniture fort in Pickle Park | feed the fort, piece by piece, into the rear-loader | he pops up between three bins: bonk him with thrown bags | 6 + 3 a player |
| Meadows | **Mama Mask** and her kits, in a cul-de-sac of lidded bins | lift bins with the Grabby Arm: the right ones tip a kit into the hopper | she comes for the truck | 8 + 3 |
| Commons | **Lucky**, owner of the Lucky Raccoon | a shell game over the nine dumpsters: back up and tip the one he dived into | he lands stunned | 10 + 4 |
| Hills | **Sir Reginald** (monocle; already named in the docs) | a blackout heist: crawl at 3 m/s so the arm takes swag from both sides, or speed up to run his crew over. You cannot do both | the arm plucks him off his fountain | 12 + 4 |
| Heights | **Big Chonk, "the Super"** | he floods the plaza from the roof chute: stay under the line with the Litter Vac for three waves | he rides the chute into a dumpster: tip it | 14 + 5 |
| Bluffs | **The Raccoon King** in Trash Panda One | the Hoverhog. Already designed in `Progression.md` §6.10 | | 40 + 15 in that design |

Each fight asks for one more thing at once: load, then load and position, position under pressure, a choice between
two verbs, capacity management, and finally a second vehicle and a crew.

### 7.4 To fix before any boss is built (code)

- **The horn does not scatter raccoons.** The docs and the night toast say it does. In code it startles them for
  0.6 s and they then attack whoever honked. The King ignores it.
- **The Hoverhog cannot touch a critter.** Bombed bags never count as hits, and the cannon and the blimp are design
  only.
- **A King beaten only by truck hits pays nobody** (run-over damage is credited to no one).

## 8. The numbers

The model plays one business through all six districts, a day at a time: collect the customers' curbs, work the
filth, sort or crush, buy. It uses the real district rows and the real map.

**Solo pacing (model, today's prices).** Days in each district:

| District | Casual | Typical | Skilled |
|---|---|---|---|
| Acres | 8 | 5 (key day 3, threshold day 5) | 4 |
| Meadows | 8 | 5 (key 2, threshold 4) | 4 |
| Commons | 7 | 5 (key 1, threshold 4) | 4 |
| Hills | 8 | 6 (key 2, threshold 4) | 5 |
| Heights | 10 | 7 (key 4, threshold 5) | 5 |
| Bluffs | 12 | 9 (key 3, threshold 7) | 7 |
| **Campaign** | **51 days = 17.0 h** | **38 days = 12.7 h** | **29 days = 9.8 h** |

**The lock holds (model).** Thirty days in a district without its key item: the typical player peaks at 64%, 59%,
54%, 59%, 54% and 49%, against thresholds of 70% to 90%.

**Income (model, typical).** Average net a day by district: $420, $1,500, $2,600, $6,200, $9,600, $25,100.

**What moves the total (model, typical).** Starting filth at x0.75 / x1 / x1.25 gives 32 / 38 / 40 days. All prices
at the same steps give 34 / 38 / 39, and bags put out 35 / 38 / 39. Fees, treasure value, bounty and dumping each
move it by a day or two. **Starting filth is the dial for campaign length.**

**Limits.** The model is expected values, not a simulation of play. It holds the work director at 1.0, does not fly
the Hoverhog, ignores raccoons, Blueprints and the crew, assumes showdown win chances (60%, 85%, 95%), and buys in a
fixed order. Days are whole steps, so every figure is "about".

### 8.1 Things the research turned up

1. **The real map is two to five times the size of the one the old pacing was simulated on.** `Progression.md`
   used loops of 540 to 1,000 m and 74 to 700 m to reach a district. The published town is a 710 m round trip to the
   Acres, 2,761 m to the Bluffs, and about 2 km to pass every Acres stop once. The old targets (Key to the City at
   10.3 h) are optimistic, and the old sim is gone: its folder is empty.
2. **The starter truck is a full day's work.** A casual player with the Curb Pup needs about the whole working day
   just to collect the Acres' customers (model). The work director is what keeps this playable today.
3. **A Butler Drop is out of reach from the depot.** Its on-time window is 100 s. The drive is 139 s at stock speed
   and 81 s with Turbo 3 (code, by arithmetic).
4. **Commons looks like it jams without the Dunker** (by reading the code, not tested). Dumpster bags count toward
   the district's 48-bag cap and nothing else can remove them, so the cap fills and put-out stops. Commons also puts
   out about 47 bags a day, not the 28 in the docs, because every put-out there adds a dumpster bag.
5. **A visiting business cannot pass rank 3** (by reading the code, not tested). Only the host's business gets the
   midnight review, so a guest earns no Clean Days.
6. **The XL Sorting Table's "5% faster sorting" does nothing.** The multiplier is never read.

## 9. Co-op and saves

### 9.1 Today (code)

- There is no Town record. A save is a business, and any business can be brought into any Town. A guest must bring
  one or start a new one.
- The districts open in a Town are whatever any online business holds a permit for. Each machine checks its own
  player at the gate.
- Clean % and curb bags are not saved. The few town-like counters ride in the host's business save.
- Players compete for the same curb bags, first come first served. The only scoreboard is the daily Big Cleanup.

### 9.2 Proposed

1. **The Town owns the campaign.** Filth, customers, hold streaks, contracts and beaten capos are one shared state,
   saved with the host's save and sent to everyone. A save is a Town plus its owner's business.
2. **A business belongs to one Town.** Nick's worry is right: a business that visits any Town breaks the arc, as a
   late-game truck in a day-one Town or as late-game cash carried home. A friend who joins picks the business they
   already have **in that Town** or starts a new one there, which is the "join with save or new" menu flow. What
   this gives up is "your shop travels".
3. **Everyone in the Town advances together.** A won contract opens the gate and the next two ranks for every
   business. Grants and the contract bonus are paid to each business in full. The bounty goes to whoever hauled the
   unit. The retainer is split as Clean Streets is today.
4. **Rivalry stays, and nobody loses.** Each business's share of a district's clean-up is tracked on a scoreboard
   like the Big Cleanup's. The top share is Lead Contractor: name on the district sign, a trophy, a bigger bonus.
5. **Late joiners** get a founding grant (suggested: the price of the key items for districts already won), or get
   hired. Payroll exists.
6. **Scaling.** Trash put-out and GoldStar's dumping scale with businesses online (1.0 / 1.7 / 2.3 / 2.8 today).
   Showdown hit points scale per player. Starting filth, thresholds, holds and prices never scale.

**Result (model, typical players).** Two, three or four businesses each finish in about 30 days (10.0 h) against 38
solo: each has less trash and buys its own key item, but the filth clears faster. One business with one, two or
three hired hands finishes in 32, 30 and 25 days. The holds alone set a floor of 20 days (6.7 h).

**The alternative** is portable businesses with gear benched down to the Town's tier. It keeps "your shop travels"
but needs a bench rule for every upgrade. Not recommended.

### 9.3 What has to change in code

- A Town record with saved filth, and a Clean % computed by the host and sent out.
- The host runs the midnight review and Rep for every business, not only its own.
- A Town id on each business, the join picker filtered by it, and the host refusing a business from another Town.
- Gates and prerequisites read the Town's contracts.

**Known gap:** a guest's business is stranded if the host stops playing. A later fix is letting the host hand the
Town to another member.

## 10. Invalidating old saves

**Today (code).** A save stores only `saveVersion` (6). It does not store the game version, which is 0.1.0 for
every playtest build, or the build stamp, which exists only in the zip name. Nothing checks for a save from a newer
build: by reading the code, it would load silently and be re-stamped 6. Migrations v3 to v6 run inline on every
load. The slot picker can already read a save's version without loading it.

**Proposed.**

1. Stamp every save and slot header with the game version, the build stamp and a **save epoch**. The build stamp has
   to be written somewhere the game can read it.
2. One table in `Assets/Scripts/Save/` lists what is void: "epoch below N", a version range, or "built before a
   date", each with a sentence for the player.
3. A void save is never loaded, migrated or overwritten. The picker shows it greyed with the reason and a **Start
   fresh** button, which moves the old file to an `outdated` folder. Nothing is deleted.
4. A save from a newer build is refused with "Saved by a newer Rubbish. Update to play."
5. A joining business sends its epoch and the host refuses a void one with the same sentence.
6. A `-allowoldsaves` switch for development, and EditMode tests on the verdict function.
7. First use: the campaign rework bumps the epoch, every save without a stamp (all of today's) is void, and the
   v3 to v6 migrations and the two unversioned ones can be deleted.

## 11. Build order (suggested, not estimated)

1. **Save invalidation.** Small, and it makes every later save change free.
2. **The Acres, solo.** Town record and saved filth; dumps; furniture and the rear-loader rule; customers in place
   of Trust; bounty and grants; a district card with the meter; threshold and hold; Louie; the contract; fines off.
   Playtest before anything else.
3. **Co-op rules.** Town-bound businesses, the host-run review for every business, Lead Contractor.
4. **One district at a time:** Meadows, Commons, Hills, Heights, then the Bluffs and the King.
5. **Rewrite the docs in §13** once the direction is approved.

## 12. Decisions for Nick

1. **Town-bound businesses** (recommended) or portable ones with benched gear? (§9.2)
2. **The six locks and key items** in §5. Three of them are new things to build.
3. **The mob's name**, and whether Mayor Bandit is the King. (§7.1)
4. **Won stays won?** Recommended. (§3.2)
5. **Campaign length.** About 13 hours for a typical solo player as modelled. Starting filth is the dial. (§8)
6. **The final fight:** the blimp needs the unbuilt Hoverhog cannon. A smaller Bluffs showdown could ship first.
7. **Should the horn scatter raccoons**, as the docs already say? (§7.4)

## 13. Docs this replaces or contradicts

**If approved, these change:**

- `Progression.md`: §0 bullets 1, 4, 5; §3.3 (neglect fine); §4.1 (`neglected_curb`); §4.2 Clean Days; §4.3 (rank
  requirements); §4.5 (`wo_clean_district`); §4.6 Neglect pressure; §5.1 to §5.3 (permits, Trust, the old map);
  §6.5 permits; the permit prerequisites in §6.9.3, §6.10, §6.11; §7 end goals; §8 (every simulation); §9.8.
- `LivingWorld.md` §2 ("keep the city clean", the bonus, neglect).
- `Multiplayer.md` §4.7, §4.8, §13.1, §13.10 (Clean Days pacing, "any business in any Town"). `Accounts.md` (slots).

**Already stale against the code, whatever is decided:**

- `Progression.md` describes four districts, three permits, a 160 kg starter truck and the old grid map. Code has
  six districts, a five-permit chain, a 120 kg Curb Pup and the coastal map.
- `LivingWorld.md` §3 says most raccoons flee and the horn scatters them. In code any provoked raccoon attacks.
- `Accounts.md` says nothing is built and describes companies, Towns and a Crew Card. The safety layer exists; the
  record model does not.
- `Multiplayer.md` still describes the rhythm tug and poaching in places. Code has shared grip, and poaching is off.

---

## 14. Slice 1: built 2026-10-09 (the Acres and the Meadows, no special items)

Nick's brief (2026-10-09): "switch it from starts clean to starts messy, clean up the town ... just in acres. no lidded bins or
furniture, but a 90% clean goal for 24 hours to unlock the next contract. same there, 90% for 24 hours to unlock commons. no special
items needed, just trash in its current state (BUT make it harder to clear the second town, maybe just more frequent trash setters,
still scale it for the amount of people online)." And: "put in a bunch of metrics tracking, even on a zip build."

### 14.1 What the player sees

1. A new business starts in the Acres with **56 old bags in heaps on about 23 curbs**, every one past the neglect window (the stink
   lines and flies the game already draw). The percent-clean bar in the left HUD column **is the contract**: its title reads the
   district and its percent (`Mildly Pleasant  0%`), the goal is drawn on the bar (a shaded zone from the line to the end, a line at
   90%, labelled `GOAL 90%`), and a line under it says what to do now: `OLD TRASH 56 of 56 bags left`. A toast, a Head Office letter
   and (first time on a save) the intro cards say what to do (§14.8).
2. Clearing the heaps pays what the bags pay today (late: 60% of the fee since the 2026-10-12 pace pass, was 25%; plus the sorting and any treasure in them). Quitting
   mid way keeps the count: the heaps that are left go back out next session. **The bar moves as you clear them:** while a
   contract is running its old bags each count an equal share of the district's dirt (all 56 together are exactly 100% of it),
   so the bar climbs bag by bag instead of sitting at 0 until the last one, and it stops at 89% until the very last old bag is
   gone (the line is never crossed with old trash left).
3. With the heaps gone the line turns into `HOLD  23h 41m left`, **counting down live in game minutes** (it is derived from a
   clock anchor on the shared game clock, so no traffic and it ticks on every machine), with a thin bar under it that fills as the
   hold is held. The district must read **90% clean (the rounded percent) for 24 game hours without a single dip** (a game
   day, 20 real minutes, the night wave included). One dip starts the hold over: the countdown disappears, the bar shakes, the
   line reads `HOLD LOST! back to 24h 00m` for five seconds and a toast says how many hours were lost. Along the way a toast
   marks the old trash being gone, the hold starting, halfway and one hour to go. The hold is not saved: a new session is a new
   attempt.
4. Holding it wins the contract: the city grants the **Minivan Meadows** permit (no purchase, no rank), the gate opens for
   everyone in the Town, a toast and letter announce it, and the Meadows' own **48 old bags** go out. The Meadows' contract is the
   same, and winning it grants **Coupon Commons**. Slice 1 ends there: Commons and everything after it work as before (rank + permit).
5. **The Meadows are harder:** while its contract is running its houses put trash out **1.5x as often** (`MeadowsRateMult`).
   Every district's put-out already scales with the people online (1.0 / 1.7 / 2.3 / 2.8x by businesses, plus the work director), and
   this multiplies that, so a bigger crew faces more trash in the Meadows, not a fixed amount.
6. The Scrapazon permit cards for the Meadows and Commons say "Win the Mildly Pleasant contract: keep it 90% clean for 24 hours" and
   cannot be bought. The map, the Career page and the gate's sign say the same.

### 14.2 What is switched off while a district is under contract (it is meant to be filthy)

Neglect fines and the -3 Rep per neglected curb (-6 since the 2026-10-12 pace pass, Progression.md §3.6); Trust falling (it can still rise); the "dirty district" Head Office letter and the
raccoon-crew nudge; the Clean City factor (a filthy contract district no longer drags every district's Clean Streets bonus down);
the Clean Day check (it looks at the district before, while the top one is under contract); and the daily "Keep X 80% clean" Work
Order (it never picks that district). After the contract is won all of these apply to the district as before.

**The Big Cleanup hour is switched off for now** (Nick, 2026-10-09: "remove that for now"). It is the scheduled double-trash event with
its own board in the depot; `CleanupTuning.Enabled = false` (`Events/BigCleanupNet.cs`) stops it from ever being scheduled and hides
the board (`CleanupBoard.Wanted`). A forced event (the debug menu) still runs and still shows the board. Flip the flag to bring it
back; Docs/Multiplayer.md §14.5 describes the event.

### 14.3 Rules as built

| | |
|---|---|
| Won | the district it opens is open in the Town. Nothing extra is stored, so a save that bought the permit earlier counts as won |
| Active | its district is open and the contract is not won (at most one at a time) |
| Hold | counts game hours only while every old bag is gone AND the district's Clean % rounds to 90 or more; a dip resets it; a held (paused) clock counts nothing |
| Old bags | ids `{stopId}/old{n}`, the district's own loot profile (no Rookie Luck, no gnomes), 2 to 20 hours past the district's neglect window (18 to 36 hours old in the Acres), placed by a stable hash so the same houses carry the heaps every session |
| Saved | `campaign_backlog_{district}` (old bags left + 1; 0 = never put out), `campaign_won_*`, `campaign_intro_*`, in the host's business save |
| Who | the host does everything (seeding, the hold, the win); the permit goes to the host's business; every machine's gate and spawner read the Town's open districts, so guests drive through and the HUD follows `WorldState.campaign` |
| Wire | `NetVersion.Protocol` 22: one `NetworkVariable<CampaignDisplay>` on `WorldState` |

### 14.4 The knobs (`CampaignTuning`, `Campaign.cs`)

`Threshold` 0.90, `HoldHours` 24, `AcresBacklog` 56, `MeadowsBacklog` 48, `MeadowsRateMult` 1.5, `BagsPerHeap` 2.5,
`BacklogMin/MaxExtraAgeHours` 2 and 20. The contract table (which district opens which) is `Campaign.contracts`.

### 14.5 What to expect, and what is unknown (read before playing)

- **The hold is meant to be hard with starter gear.** A hand-run estimate: half a day's bags arrive between 20:30 and 05:00, so at
  90% (3 dirt in the Acres) a driver has to sweep about every 4 game hours (3 to 4 real minutes) through the night, and a Curb Pup
  trip to the depot alone costs more than one game hour. A new player will probably break the hold, buy the rear-loader, arm and
  capacity, and try again; that is the "work toward it with gear" Nick described. `hold_break` in the metrics shows when and why.
  If it is plainly unfair, `Threshold`, `HoldHours` and (for the Meadows) `MeadowsRateMult` are the first things to turn.
- **The first night is rough.** Raccoon pressure follows the town's dirt, which starts at maximum, so the first dusk (11 real
  minutes in) brings the most and the most rabid raccoons the game spawns. Nothing was changed there. If new players flee, scale
  the pressure by the contract district's clean-up progress.
- **Not simulated.** The expected-value model in `sim/` was built for the proposal, not this slice, and it predates the
  contract details. Treat its pacing as a guess until the metrics replace it.
- **Cosmetic leftovers.** The gate's *press* message for a player below rank 4 still says "Rank 4. You are not that." (the sign and
  prompt are fixed at runtime by `ContractGateSigns`). Fixing it needs an edit to `DistrictGate.cs`, which is part of the baked town,
  so it waits for the next bake. A guest who hosts their own Town later has to win the contracts there too (the campaign is
  the Town's, §9).
- **Old saves.** A save with the Meadows permit but not Commons gets a messy Meadows and a running contract next session.
  Nothing is migrated (Nick: old saves don't matter; the save-invalidation system in §10 is still to do).

### 14.6 Trying it

- Play mode, host or solo. **Rubbish > Debug > Campaign**: `Win The Running Contract Now`, `Clear The Old Trash`,
  `Put The Old Trash Back`, `Log The Contract`, `Take Back The Meadows And Commons Permits` (retest from the start).
- Fresh start: a new business (or a test data root) shows the 56 heaps and the HUD line within a second of entering the town.
- EditMode tests: `CampaignRulesTests` (the hold rule, the heaps, the contract table, the store text), `CampaignFlowTests`
  (the heaps on the real houses, a saved remainder coming back, a win granting the next permit and starting its heaps, the
  Meadows' 1.5x only while its contract runs, the district under contract spared fines / Trust loss / the worst-district factor),
  `CampaignHoldTests` (the host tick run for real over a skipped clock: a full day wins, old trash blocks, one dip restarts, the
  saved count follows the curbs), `MetricsTests` (every line the runner writes is valid JSON with the fields `summarize.py`
  reads), and for the walk-through (§14.8) `TutorialRulesTests`, `TutorialBrainTests` (a whole first run played with made-up
  facts, skipping ahead, every way out) and `ContractCardsTests` (the words say the real numbers).
- **Seen running (2026-10-10, a private copy, a new empty save):** the cards, the HUD bar with its instruction line, the countdown
  fill, every walk-through step from "get in your truck" to the laptop's third tab and the floppy lesson, the arrows, the done
  flash, the saved progress carrying over a quit. **Not seen:** a co-op guest, the `WorldState.campaign` replication to one, a
  gamepad, the on-foot grab-and-toss wording with real bags (the arrows and texts are tested, the clicks were simulated), the
  gate sign, and a real session writing its metrics file for a long time.
- No bake is needed: none of the edited scripts is attached to the baked town (the gate script and `PickupStop` are untouched).
  New scripts get their `.meta` files when Unity next imports the project.

### 14.7 Files

New: `Progression/Campaign.cs` (rules, state, host tick, display), `Progression/ContractFeedback.cs` (the toasts and sounds on every
machine), `World/ContractGateSigns.cs`, `Save/Metrics.cs`, `Save/MetricsRunner.cs`, `Editor/CampaignDebug.cs`,
`Tests/EditMode/Business/CampaignRulesTests.cs`, `CampaignFlowTests.cs`, `CampaignHoldTests.cs`, `MetricsTests.cs`,
`Tools/metrics/summarize.py`, `Docs/Metrics.md`. The walk-through (§14.8): `Progression/Tutorial.cs`, `TutorialBrain.cs`,
`TutorialRules.cs`, `UI/Contract/ContractIntroScreen.cs`, `TutorialHud.cs`, `LaptopCoach.cs`, `Editor/TutorialDebug.cs`,
`Editor/UiShots.cs`, `Tests/EditMode/Business/TutorialRulesTests.cs`, `TutorialBrainTests.cs`, `ContractCardsTests.cs`.
Edited (small): `Trash/CurbTrashSpawner.cs` (rate multiplier, the two seeding hooks), `Trash/CityCleanliness.cs` (worst district,
neglect), `Progression/ProgressionRules.cs` (Clean Day, Trust, letters), `Progression/Progression.cs` (permit lock),
`Progression/DistrictAccess.cs` (a won district is the Town's), `Progression/WorkOrders.cs`, `Progression/ScrapazonCareer.cs`,
`UI/Map/FullMap.cs`, `Feel/FeelHudCity.cs` (the contract bar), `Net/World/WorldState.cs`, `Net/Core/NetConstants.cs` (Protocol 22),
`Events/BigCleanupNet.cs` and `Events/CleanupBoard.cs` (the Big Cleanup hour switched off).

### 14.8 The first-run walk-through (built 2026-10-10)

Nick: "now is the time for a brief tutorial on login, highlighting the goal ... it guides them to get in the truck, collect trash.
Once the truck is full, be extra clear: return to the depot to drop off your trash. On first depot entrance, show them around ...
point them to the PC, point them to enter their truck and hold [the key] to drop it all off, bring a bag to the sort station ... tell
them to go get another bag, you can throw it at the table as well to auto rip it. Then the computer guides them through the tabs.
Same when they get the floppy disk: first time entering the computer after that, show them the tab, make them click it, explain it."

**What the player sees.** A new business, on its first login:

| # | Lesson | What it says and points at | It is done when |
|---|---|---|---|
| | The cards | three paper cards: the district is a mess and what it earns; clear the old trash (a picture of the bar); then hold it (a picture of the countdown). Next / Back / Skip, Enter is Next | closed (read or skipped). After them the bar glows for seven seconds and a toast says "That bar is your goal" |
| 1 | Get in your truck | arrow at the truck, `E` | seated |
| 2 | Drive out | arrow at the garage door, `F` (only the solo garage start has this; a guest or one-world host starts outside) | out of the depot |
| 3 | Pick up the old trash | arrow at the nearest old heap, then "grab a bag `E`", "toss it in the truck `E`" (arrow at the hopper), with the kg loaded | the truck is full |
| 4 | **Truck full! Back to the depot** | the loud one: a big pulsing banner, an arrow at the depot door, `F` | inside the depot with a load |
| 5 | The depot tour | three four-second stops, each with an arrow: the laptop, the tipping floor, the sorting table | the tour ends, or the player starts tipping |
| 6 | Unload the truck | arrow at the tipping floor, hold `G` | the load drops to nothing |
| 7 | Sort a bag | arrow at the bag pile ("hop out of the truck `E`" first), then at the table: step up, slam it open | a bag rips |
| 8 | Sort the finds | pick up each item, drop it in its bin | two items sorted (one if the bag had one) |
| 9 | Now throw one | fetch another bag, hold `LMB` to charge, let go at the table: thrown bags tear themselves open | a bag rips while the player is not at the station |
| 10 | The laptop | arrow at the laptop, then a gold ring and a speech bubble on the eBag tab, the Scrapazon tab and Scrapazon's Career button, one at a time: the player clicks each | all three clicked |
| later | A floppy disk | a banner if it is left on the table ("KEEP it", arrow at it) | the disk is installed |
| later | The new tab | "your laptop has a new tab", arrow at the laptop, then the ring on the SolidWaste 3D tab with what it is | the tab is clicked (the first open lands on it already; clicking it still counts) |

Other contracts get one short card each (the Meadows' says its houses are 50% busier). Everything is an **overlay, never a gate**: nothing
is locked behind a step, no input is blocked (the laptop's dimming is only a look), and a player who does things out of order is simply
marked as having done what they did (taking a half load home skips "fill the truck"; throwing a bag first skips "now throw one").
**Hold U for 1.2 seconds** to switch the whole thing off for that save (a hint sits above the banner).

**Who gets it.** The state is the business's own counters (`tut_mask` one bit per lesson, `tut_off`, `tut_init`, `tut_cards_<district>`,
`tut_tabs`, `tut_disk`), so it follows the business and each co-op player has their own, on their own screen. The first time a save is
seen (`tut_init`) it is judged: a save that has already sorted an item, earned cash, recycled weight, ranked up, bought a truck or begun
the starter contract is "experienced" and gets only the contract cards, once. A new save gets everything. Quitting half way resumes at
the first lesson not done. `Rubbish > Debug > Tutorial` restarts it (as a new save), switches it off, and logs where it is.

**How it is built.** `TutorialBrain` is pure: given what is true (`TutorialFacts`) and what has been seen (`TutorialMemory`) it says
whether a lesson is done and what its banner says and points at; `TutorialBrainTests` plays a whole first run through it and checks
every lesson has a way out. `Tutorial` (a hidden runner) reads the game into those facts by **polling public statics** (the player,
the truck load, the carrier, the table's ripping flag, the item-sorted event, the laptop's open app) and finds the places by type, never
by scene path. **No scene script was edited, so no town bake is needed.** `TutorialHud` is its own overlay canvas scaled like the HUD
(a banner above the bottom dock, an arrow in the middle lane that goes to the screen edge when its target is out of view, the green
done flash). `ContractIntroScreen` draws its own picture of the bar because every modal screen hides the real HUD in a solo game (the
HUD reads "Paused" behind it). `LaptopCoach` lives inside the laptop's own UI Toolkit panel and finds the tabs by the names in
`ebae.uxml`.

**Things to know.**
- Key names in the text come from the game's own constants and switch to pad names (X, Y, B, RT) when a gamepad is the active input;
  the pad wording was not tried on a pad.
- The first laptop open after the disk lands straight on SolidWaste 3D (the game's own rule), so that lesson asks for a click on a tab
  that is already showing.
- The old-trash arrow follows the contract's heaps; the route panel's own arrow ("Next: 27 Birch St") is separate and may point at a
  different curb.
- `Rubbish > Debug > UI Shots > Contract And Tutorial` renders the bar, the cards and the banner to PNGs without playing
  (`UiShots.RunBatch` for batch mode): the way to look at a layout change.

### 14.9 The third contract: Coupon Commons and its dumpsters (built 2026-10-14)

Nick, 2026-10-14: "implement the pickup of dumpsters in commercial area ... with the correct required upgrade and ensure game
balance changes we made above proceed and are balanced into the third zone", and "that zone should contain both. dumpsters and
same loose bags from previous zones". The dumpsters and the Dumpster Dunker were already built (`Progression.md` §6.11); what
was missing was a reason to need them and a campaign past Commons.

- **The contract.** Winning the Meadows grants Coupon Commons, which starts messy like the first two: **42 old bags, 27 of them in
  the shops' dumpsters** (spread one per dumpster in turn, at most 6 each) **and 15 loose** in curb heaps. Same rules: clear every old
  bag, get the bar over 90%, hold it 24 game hours. Winning it grants **Snootington Hills** (its $3,500 permit is no longer for sale;
  Heights and the Bluffs still open by rank and permit). `CampaignTuning.CommonsBacklog` / `CommonsInDumpsters`, the third row of
  `Campaign.contracts`.
- **The required kit.** Loose bags need nothing new. The dumpster bags can only be emptied by a **rear-loader with the Dumpster
  Dunker** (hands, the side arm, the Hoverhog and the raccoon crew can't reach them), and the win needs every old bag gone, so the
  contract can't be won without it. That is the "locked filth" of §4, and the Dunker is Commons' key item of §5.
- **Fresh trash** there is both too, as before: every shop's put-out adds a dumpster bag and about 60% of new bags go in the dumpster
  while it has room.
- **Gates moved.** The Dunker is **rank 4** (was 5) so it is on sale the moment Commons opens; its other prerequisites are unchanged
  (the rear-loader and Commons open). A prerequisite that names a contract district's permit now counts as met when that district
  is open in the Town (`Progression.ContractPermitOpen`), and the lift's permit check accepts a contract-won district
  (`TruckDumpsterLift`), so a co-op guest can buy and use a Dunker in a Commons the host won.
- **The card and the letter** say it: "Most of it is stuffed in the shops' dumpsters... you need the rear-loader and the Dumpster
  Dunker" (`CampaignContract.note`).
- **Balance after the 2026-10-12 pace pass** (`sim/pace_model.py zones`, model only): per load, Commons pays more than the Meadows
  and less than the Hills in both cash and Rep (typical, rear-loader: old heap ~$1,200 / 1,550 Rep, fresh on-time ~$3,200 / 1,700 Rep).
  The Dunker ($2,400) is about one Meadows load, so it is affordable on arrival; rank 4 is long passed by then (two contracts mean at
  least two midnights).
- **Old saves.** A save with the Hills permit counts as having won Commons. A save with Commons open but not the Hills gets a messy
  Commons and a running contract next session.
- **Not tested in play:** the whole contract. EditMode: `CampaignFlowTests` (the heap: 42, 27 in dumpsters, 15 loose; the win grants
  the Hills; the Hills permit can't be bought; the Dunker can once Commons is open), `ContractCardsTests`, `DumpsterRulesTests`.
  Known: a Hills gate pressed below rank 7 before the win still says "Rank 7" (the baked gate script; the sign itself is right).

