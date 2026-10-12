# Pace model results (2026-10-12)

Output of `python3 Docs/sim/pace_model.py` and `... sens`. Read `pace_model.py` for what it is and is not: an expected-value Monte Carlo of the first loop and the first in-game day, not a port of the game, and nobody has timed a real "locked in" player. Times are real minutes from the start of a new messy-start save, read off a piecewise-linear timeline (so "day 1" is exactly 20 minutes). "Magnus + Bag Belt affordable" = $1,400 in hand AND rank 3. A quarter of each load's sort value is junk scrap that no streak multiplies. Revised after an independent review the same day (day-1 columns used to be read at the next loop boundary, which overstated them by 20-60%).

```

=== OLD economy (before 2026-10-12) ===
player     | one load: $     Rep  min  | x avg / top | rank 2 at | day 1 (20 min): loads done $       Rep | Magnus + Bag Belt affordable at | rank 3 at
casual     |      86    68 16.1  |  1.0 /  1.4 |    --     (0%) |  1.0     100     80 |    --     (0%)              |    --     (0%)
typical    |      96    88 12.5  |  1.1 /  2.0 |    --     (0%) |  2.0     138    126 |    --     (0%)              |    --     (0%)
skilled    |     113    92  9.8  |  1.5 /  2.0 | 47.5 min (93%) |  2.0     229    190 |    --     (0%)              |    --     (0%)
locked in  |     125   100  8.2  |  1.8 /  2.0 | 38.6 min (100%) |  2.0     274    222 |    --     (0%)              |    --     (0%)

=== NEW economy (2026-10-12 pace pass) ===
player     | one load: $     Rep  min  | x avg / top | rank 2 at | day 1 (20 min): loads done $       Rep | Magnus + Bag Belt affordable at | rank 3 at
casual     |     404   532 16.1  |  1.0 /  2.0 | 11.7 min (100%) |  1.0     479    623 |    --     (0%)              | 29.7 min (100%)
typical    |     490   596 12.5  |  1.5 /  3.0 |  8.7 min (100%) |  2.0     716    866 | 36.1 min (100%)              | 21.3 min (100%)
skilled    |     696   612  9.8  |  2.6 /  5.0 |  6.8 min (100%) |  2.0    1404   1250 | 19.9 min (100%)              | 16.4 min (100%)
locked in  |    1001   636  8.2  |  4.3 / 10.0 |  5.8 min (100%) |  2.0    2157   1424 | 13.8 min (100%)              | 13.7 min (100%)
```

## Sensitivity (each row moves one knob of the NEW economy)

```

=== NEW, but pickup x2.5 and sorting x3 (fee 10, sort 3) ===
player     | one load: $     Rep  min  | x avg / top | rank 2 at | day 1 (20 min): loads done $       Rep | Magnus + Bag Belt affordable at | rank 3 at
casual     |     323   532 16.1  |  1.0 /  2.0 | 11.7 min (100%) |  1.0     388    624 |    --     (0%)              | 29.5 min (100%)
typical    |     387   596 12.5  |  1.5 /  3.0 |  8.7 min (100%) |  2.0     574    865 | 46.2 min (100%)              | 21.4 min (100%)
skilled    |     538   620  9.8  |  2.6 /  5.0 |  6.8 min (100%) |  2.0    1094   1250 | 26.7 min (100%)              | 16.4 min (100%)
locked in  |     752   636  8.2  |  4.2 / 10.0 |  5.8 min (100%) |  2.0    1689   1426 | 15.5 min (100%)              | 13.7 min (100%)

=== NEW, but pickup x3.5 and sorting x5 (fee 14, sort 5) ===
player     | one load: $     Rep  min  | x avg / top | rank 2 at | day 1 (20 min): loads done $       Rep | Magnus + Bag Belt affordable at | rank 3 at
casual     |     488   532 16.1  |  1.0 /  2.0 | 11.7 min (100%) |  1.0     571    624 | 46.8 min (97%)              | 29.5 min (100%)
typical    |     593   596 12.5  |  1.5 /  3.0 |  8.7 min (100%) |  2.0     854    865 | 31.7 min (100%)              | 21.4 min (100%)
skilled    |     845   620  9.8  |  2.6 /  5.0 |  6.8 min (100%) |  2.0    1716   1250 | 17.5 min (100%)              | 16.4 min (100%)
locked in  |    1202   636  8.2  |  4.2 / 10.0 |  5.8 min (100%) |  2.0    2670   1426 | 13.7 min (100%)              | 13.7 min (100%)

=== NEW, but no cooling: a missed window ends the streak ===
player     | one load: $     Rep  min  | x avg / top | rank 2 at | day 1 (20 min): loads done $       Rep | Magnus + Bag Belt affordable at | rank 3 at
casual     |     404   532 16.1  |  1.0 /  2.0 | 11.7 min (100%) |  1.0     479    624 |    --     (0%)              | 29.5 min (100%)
typical    |     470   596 12.5  |  1.4 /  3.0 |  8.7 min (100%) |  2.0     687    865 | 37.4 min (100%)              | 21.4 min (100%)
skilled    |     600   620  9.8  |  2.1 /  5.0 |  6.8 min (100%) |  2.0    1216   1250 | 25.0 min (100%)              | 16.4 min (100%)
locked in  |     818   636  8.2  |  3.3 / 10.0 |  5.8 min (100%) |  2.0    1805   1426 | 14.9 min (100%)              | 13.7 min (100%)

=== NEW, but windows 3.5 s flat (no speed pressure) ===
player     | one load: $     Rep  min  | x avg / top | rank 2 at | day 1 (20 min): loads done $       Rep | Magnus + Bag Belt affordable at | rank 3 at
casual     |     437   532 16.1  |  1.2 /  3.0 | 11.7 min (100%) |  1.0     506    624 | 47.8 min (1%)              | 29.5 min (100%)
typical    |     622   596 12.5  |  2.2 /  5.0 |  8.7 min (100%) |  2.0     866    865 | 31.2 min (100%)              | 21.4 min (100%)
skilled    |     877   620  9.8  |  3.6 / 10.0 |  6.8 min (100%) |  2.0    1805   1250 | 17.1 min (100%)              | 16.4 min (100%)
locked in  |    1214   636  8.2  |  5.4 / 10.0 |  5.8 min (100%) |  2.0    2674   1426 | 13.7 min (100%)              | 13.7 min (100%)

=== NEW, but ladder tops out at x5 ===
player     | one load: $     Rep  min  | x avg / top | rank 2 at | day 1 (20 min): loads done $       Rep | Magnus + Bag Belt affordable at | rank 3 at
casual     |     406   532 16.1  |  1.0 /  2.0 | 11.7 min (100%) |  1.0     479    624 |    --     (0%)              | 29.5 min (100%)
typical    |     490   596 12.5  |  1.5 /  3.0 |  8.7 min (100%) |  2.0     714    865 | 36.2 min (100%)              | 21.4 min (100%)
skilled    |     690   620  9.8  |  2.6 /  5.0 |  6.8 min (100%) |  2.0    1404   1250 | 19.9 min (100%)              | 16.4 min (100%)
locked in  |     894   636  8.2  |  3.7 /  5.0 |  5.8 min (100%) |  2.0    1923   1426 | 14.4 min (100%)              | 13.7 min (100%)

=== NEW, but top tier at 25 instead of 35 ===
player     | one load: $     Rep  min  | x avg / top | rank 2 at | day 1 (20 min): loads done $       Rep | Magnus + Bag Belt affordable at | rank 3 at
casual     |     406   532 16.1  |  1.0 /  2.0 | 11.7 min (100%) |  1.0     479    624 |    --     (0%)              | 29.5 min (100%)
typical    |     490   596 12.5  |  1.5 /  3.0 |  8.7 min (100%) |  2.0     714    865 | 36.2 min (100%)              | 21.4 min (100%)
skilled    |     725   620  9.8  |  2.8 / 10.0 |  6.8 min (100%) |  2.0    1471   1250 | 19.1 min (100%)              | 16.4 min (100%)
locked in  |    1229   636  8.2  |  5.5 / 10.0 |  5.8 min (100%) |  2.0    2627   1426 | 13.7 min (100%)              | 13.7 min (100%)

=== NEW, but Rep per item 2 instead of 3 ===
player     | one load: $     Rep  min  | x avg / top | rank 2 at | day 1 (20 min): loads done $       Rep | Magnus + Bag Belt affordable at | rank 3 at
casual     |     406   395 16.1  |  1.0 /  2.0 | 14.4 min (100%) |  1.0     479    487 |    --     (0%)              | 38.8 min (100%)
typical    |     490   451 12.5  |  1.5 /  3.0 | 10.1 min (100%) |  2.0     714    691 | 36.2 min (100%)              | 27.0 min (100%)
skilled    |     692   472  9.8  |  2.6 /  5.0 |  7.7 min (100%) |  2.0    1405    955 | 20.1 min (100%)              | 19.9 min (100%)
locked in  |     977   486  8.2  |  4.2 / 10.0 |  6.4 min (100%) |  2.0    2180   1126 | 16.2 min (100%)              | 16.2 min (100%)
```
