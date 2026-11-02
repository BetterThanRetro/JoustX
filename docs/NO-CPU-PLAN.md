# Plan: running the game with no CPU emulation

Goal: the game runs entirely in C#, with no 6809 emulation and identical gameplay. The emulated
original stays in the test suite as the reference (roadmap step 5).

## Outcome (2 November 2026)

**Done.** The app now runs the game with no 6809 emulation, identical to the original frame by
frame. It took two steps instead of the five phases below.

1. **The whole program translated to C#.** `joust-tools translate` turns every one of the 9,696
   instructions in the source into a `case` of a generated C# routine
   (`src/Joust.Port/Translated/*.g.cs`), with its source line as a comment.
   - Each instruction's cycle count is measured on the reference CPU.
   - Branches inside a routine are `goto case`; calls, returns and jumps leave through the
     dispatcher.
   - The translation runs under the hand-written routines and also takes the calls they hand
     back.
2. **The no-CPU runtime** (`NoCpuRuntime`). Translated code stops at an exact instruction boundary
   whenever the original's timing matters:
   - before an instruction that would cross an interrupt edge;
   - before an instruction that reads an I/O register;
   - after an instruction that writes one or may unmask interrupts.

   At those points the runtime runs that single instruction in C#, then enters the interrupt in
   C# exactly where the 6809 would have. The emulated CPU's instruction stepping is never called
   (`JoustMachine.CpuSteps` stays 0).

Because interrupts land on the same instruction as in the original, phases 2 and 3 below
(deferred interrupts, read/write overlap checks, splitting routines) were not needed. The
exception in decision 1 (dead stack bytes) never arises: the stack matches byte for byte.

**Verified:**
- `verify-native --no-cpu`: 3 seeds × 20,000 frames, plus two players, the troll and lava
  deaths, egg waves, cliffs, and the translation without any hand-written code.
- Tests from power-on (self-test, factory restore, power cycle, a game) and through the operator
  menus.
- Lockstep identical every frame, 0 per-call mismatches, 0 CPU steps.

**Speed:** 0.12 ms per frame (`joust-tools bench`).

**The sound board too (later on 2 November).** The sound board's 6808 program (1,376
instructions) is translated the same way (`Sound.g.cs`). Each instruction first checks for a
pending interrupt and the end of its time slice, then charges its cycles through
`SoundBoard.Spend` before it runs, exactly as the emulated core is stepped, so the samples come
out bit for bit the same. The flag arithmetic (`Alu6800`) is shared with the emulated core. One
sound (command `$18`) builds a `JMP` in RAM and jumps through it; the runtime follows that jump.
Tests play every sound command, and 400 commands interrupting each other, on an emulated and a
translated board side by side (identical samples, registers and RAM). A game played by a fully
translated session matches the fully emulated original in every frame, including every audio
sample. Neither board's emulated CPU runs a single step.

The sections below are the plan as it was written before the work.

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
2. **Operator menus and hardware self-test.** Decided: keep everything. The operator menus,
   bookkeeping, adjustments, hardware diagnostics and the attract demo are all translated, none cut
   or left on the emulated board. The no-CPU build matches the original from power-on.

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
