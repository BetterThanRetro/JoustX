# Census: original code that still runs

This is phase 0 of [the no-CPU plan](NO-CPU-PLAN.md).

To reproduce it, run:

```bash
dotnet run -c Release --project src/Joust.Tools -- census --frames 30000 --out build/census.txt
```

The raw per-label report is in [census-report.txt](census-report.txt).

## Method

The census ran 10 scenarios of 30,000 frames each (8⅓ minutes of play per scenario). Every C#
routine was installed. It recorded each original 6809 instruction that still executed and grouped
the instructions by the nearest label.

A label counts as a **hand-back** if a C# entry point lies inside it: there, C# let the original
run a call, or the original took a path that the C# code leaves to it. A label with no C# entry
point counts as **not ported**.

| Scenario | Original code, share of non-idle time |
| --- | --- |
| Attract mode | 5.3% |
| One player (two seeds) | 2.1–2.3% |
| Two players | 2.2% |
| Troll awake and lava deaths | 2.4% |
| Every wave an egg wave | 2.3% |
| Cliffs changing (wave 81) | 2.5% |
| High-score entry (score raised before game over) | 2.1% |
| Tilt and the right and center coin slots | 1.9% |
| Operator menus and self-test | 89% |

All the targets were reached:

- **Operator menus and self-test:** bookkeeping (`BOOK`), adjustments (`ADJS`), the switch test,
  the sound test, colour bars, the crosshatch pattern, and the RAM and CMOS tests.
- **High-score entry** (`ENTINT`, `ENTI2`): it also happens in the ordinary games, because the
  factory table is easy to beat.
- **Tilt and the coin slots:** `COINR` and `COINC`.

## Findings

| Where it runs | Kind | Labels | Distinct instructions |
| --- | --- | --- | --- |
| Game and attract mode | not ported | 675 | 4,630 |
| Game and attract mode | hand-back | 206 | 2,448 |
| Operator menus and self-test only | not ported | 128 | 858 |
| **Total** | | **1,009** | **7,936** |

**By time**, the game is nearly done: 2–2.5% of play and 5% of attract mode.

**By amount of code**, it is not. About 5,500 distinct original instructions run somewhere and
have no C# version. They are cold code that runs once per game, per wave or per death:

- game start and end (`SIM1A`, `ENDGAM`, `PLINIT`, `DECLIV`);
- the attract demo and marquee (`DEMO`, `SIM2A`, `MARQUE`, `ATTRCT`, `CREID`);
- the high-score table and entry (`HIGHPO`, `ENTINT`, `ENTI2`);
- the less common enemy and pterodactyl paths (`BAITST`, `PTEKL2`, `PTELEV`);
- one-off death and egg cases (`SPDIE2`, `ADLFRE`, `EGGSMN`);
- smaller pieces of the interrupt handler (`IRQNSL`) and the sound queue (`EXESND`).

A long tail of single-wave events probably still isn't covered, so the true number is somewhat
higher.

The 2,448 hand-back instructions are what phases 2 and 3 of the plan remove.

## What this changes in the plan

At the rate the hand-written port has gone (about 250 instructions for 2% of a weekly allowance),
hand-porting 5,500 cold instructions would cost roughly 40–50% of a week on its own. That is well
over the 10–20% the plan estimated for phase 1.

**Recommendation: translate the cold code automatically.**

- The C# `Asm` helpers already mirror 6809 instructions one for one, with exact flags and cycles,
  so cold routines can be generated as C# from the assembled program.
- The per-call verifier and lockstep check the generated code exactly as they check the
  hand-written code.
- Hot routines stay hand-written and readable. Cold code becomes generated C# that is correct by
  construction and checked against the original.

| Item | Cost (share of a weekly allowance) |
| --- | --- |
| The translator | about 8–12% |
| Fixing what verification finds | 5–10% |
| Total, replacing phase 1 | about 13–22% |

That brings the whole plan back to roughly 40–60% of a weekly allowance.
