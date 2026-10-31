# Plan: running the game with no CPU emulation

Goal: the game runs entirely in C#, with no 6809 emulation and identical gameplay. The emulated
original stays in the test suite as the reference (roadmap step 5).

## Where things stand

- C# routines handle about 97.5–98% of the game's non-idle CPU time, and lockstep is identical
  frame by frame.
- Every C# routine is written as a transaction (`Tx`) that charges each instruction's exact
  cycles. That is the most important asset for this step: the C# code already knows the cycle at
  which each original instruction would have ended.
- The emulated CPU is still needed for two things:
  1. **Code that isn't in C# yet.** That includes the rare paths that hand back on purpose: lava
     death (`ADGFLR`), the troll appearing, coin and advance presses in the interrupt handler,
     the once-a-minute audit, the operator menus and self-test, high-score entry, and a few small
     routines.
  2. **Interrupts landing inside a routine.** The 4 ms interrupt (lines 0, 64, 128, 192) can arrive
     between any two instructions. Its handler reads the beam position, stirs the random seed,
     feeds the watchdog and plays the frame's drawing lists to the blitter, which writes the
     screen. A C# routine currently only runs when no interrupt edge falls inside it; otherwise
     the original runs that call. These are the "hand-backs".

## The idea: deliver the interrupt in C#, at the right moment

When an interrupt edge falls inside a C# routine:

- Run the routine to the end.
- Run the C# interrupt handler as if it had arrived at the instruction boundary where the CPU
  would have taken it. The `Tx` cycle count gives that moment.
- Charge the handler's cycles, so the clock, the next interrupt edges and the blitter stalls come
  out the same.

This gives the same result as the original whenever the routine and the handler don't touch the
same memory after that moment. `Tx` already records every write, so recording reads as well makes
this checkable on every call:

- **No overlap:** the deferred interrupt is exact. This is expected for the large majority of
  routines.
- **Overlap**, for example a routine reading screen pixels that this interrupt's blits change, or
  reading `RANDOM` after the edge: the routine must be split at that instruction so the interrupt
  can run in between. Verification lists exactly which routines need this, so only those get
  split.

The one difference that can never match byte for byte is **stack garbage**. On a real interrupt
the CPU pushes 12 bytes of registers below the stack pointer. Those bytes are dead (nothing reads
them again), but they differ depending on where the interrupt landed. The per-call verifier already
ignores bytes below the stack pointer; the frame lockstep check would have to do the same.

## Phases

Each phase ends verified in lockstep and committed, and the game stays playable throughout.

**0. Census.** Build a coverage suite of input scripts covering:

- attract mode;
- waves 1–30;
- two players, including gladiator rounds;
- pterodactyls, the troll and lava deaths, and egg waves;
- high-score entry;
- coins and tilt;
- the operator menus (audits, adjustments, factory restore) and the self-test.

Record every original instruction that still executes, split into "not ported" and "hand-back".
This turns the remaining work into a definite list.

*Done: see [CENSUS.md](CENSUS.md). The census found about 5,500 cold, unported instructions, which
is more than hand-porting can cover cheaply. It recommends generating C# for the cold code (phase
1 below) instead.*

**1. Port what the census finds.** First the game paths (lava death, troll appearance, coin
handling and audits in the interrupt handler, high-score entry), then the operator menus. Finish
when the census shows only hand-backs.

**2. Deferred interrupts, still with the CPU present.** Add read tracking to `Tx`. Replace the
hand-back with the deferred C# interrupt wherever the overlap check passes, and keep the hand-back
where it fails. Change the lockstep check to compare live state, ignoring dead stack bytes. Measure
the overlap rate per routine. The CPU remains as a safety net for every call that fails the check,
so this phase is low risk.

**3. Split the overlapping routines.** Using phase 2's measurements, give those routines a resume
point at the conflicting instruction, so the interrupt can run there. Done when no call needs a
hand-back across the coverage suite and long random runs.

**4. The no-CPU runtime.** A `NativeMachine` with no `Cpu6809`. It keeps:

- RAM, CMOS, the palette, the blitter (with its stalls), the PIAs and the sound port;
- the video clock, with interrupts generated from the cycle count;
- the C# register set that the routines use;
- a dispatch loop that runs the routine at each continuation address.

An address with no C# routine is a hard error that names the address. The app switches to it;
`--original` keeps the emulated board for comparison.

**5. Soak and clean up.** Run many seeds for 100,000+ frames each, plus the coverage suite, all in
lockstep against the original. Profile and simplify the `CanRun` interrupt-safety plumbing, which
the runtime no longer needs. Update the README and roadmap.

## Decisions

1. **What counts as identical.** All live state is identical frame by frame, but the 12 dead stack
   bytes left by an interrupt are ignored. Recommended: yes. Matching them would require
   recreating the mid-routine register values of the original, at great cost, for bytes nothing
   reads.
2. **Operator menus and hardware self-test.** Port them too (needed for a true no-CPU build, and
   the operator menus hold the game settings), or keep the hardware diagnostics (RAM/ROM/colour
   tests, sound test) on the emulated board as a documented exception. Recommended: port the
   operator menus and high-score handling; decide on the diagnostics after the census shows how
   large they are.

## Cost

These are rough estimates, firmed up by the census:

| Phase | Share of a weekly allowance |
| --- | --- |
| 0. Census | 3–5% |
| 1. Port the rest | 10–20% |
| 2. Deferred interrupts | 8–12% |
| 3. Split routines | 5–15% |
| 4. No-CPU runtime | 5–10% |
| 5. Soak and clean up | 3–5% |
| **Total** | **about 35–65%** |

At the current 75% weekly cap, that spans two to three weeks. Phases 0 and 2 are the ones that
turn estimates into facts, so they come first.
