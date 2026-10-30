# JoustX roadmap

Goal: a game that **plays exactly like 1982 Joust**, written in C#, with graphics and sound rebuilt for 2026.

## Why a verified port

The original code is tightly bound to the hardware, so a straight hand-translation would drift from the original in ways that are hard to see:

- Object state lives in 43-byte process blocks at fixed RAM offsets. Behaviour is chosen through routine *addresses* stored in those blocks (`PJOY`, `PSTATE`, `PADGRA`, decision blocks).
- Collisions are pixel-exact, using per-frame masks in `JOUSTI.ASM` and background tables built from the cliff bitmaps (`BCKXTB`, `LNDXTB`).
- Which secondary processes run in a frame depends on the blitter interrupt draining the DMA tables (`SYSTEM.ASM`, `EXESEC`: `LDA [PDTBL]`). In other words, CPU and blitter timing feed into game logic.
- Code jumps by byte counts (`BLT *+2+3+2`), and the lava routine reads screen memory.

So we keep the original as an executable oracle and replace it piece by piece, proving each piece identical.

## Phase 1: reference machine (done)

- [x] C# assembler for the AS dialect, with AS operator precedence, multi-pass shortest encodings, local labels and macros
- [x] All 12 ROMs rebuilt from source and matching the game's own ROM-test checksums
- [x] 6809 CPU with documented cycle counts; SC1 blitter (with the XOR-4 size bug and bus stall); 6821 PIAs; 4-bit CMOS; 260-line/60.1 Hz video timing with the 4 ms IRQ edges on lines 0/64/128/192
- [x] Boots, restores factory settings, runs attract mode, the instructional demo and real games
- [x] MonoGame host with keyboard input and CMOS persistence
- [x] `GameStateReader`: live objects (kind, position, facing, image pointers) read from process blocks
- [x] Tests: encodings, CPU semantics, checksums, deterministic golden replay

Still open here:

- Cross-check the ROM images against MAME's CRC32s. 8-bit sums are a weak check on their own, though the game running correctly is strong evidence.
- Undocumented 6809 opcodes are rejected. That is fine as long as Joust never runs one, which the replay would expose.

## Phase 2: native C# port (strangler pattern)

**Status:** the hook mechanism, both verifiers and the first routines are in place.

| Routine | What it does | Native since |
| --- | --- | --- |
| `RANDSYS` | 16-bit shift-register random numbers | Phase 2 start |
| `SND` | Sound priority queue | Phase 2 start |
| `DCOLOR` / `DCOLR2` | Load a 16-colour palette | Phase 2 start |
| `SCRHUN` / `SCRTEN` | BCD score add (the extra-mount award path is still handed to the original) | Phase 2 start |
| `CHROUT` / `CHR35` / `PHROUT` / `PHR35` | Text output through the blitter (5x7 and 3x5 fonts) | Physics step |
| `ADDGRA` / `ADDGRX` | Gravity, Y integration, ceiling bounce, X speed table (sinking into lava is handed back) | Physics step |
| `ADDFLP` | Flap lift (less lift the longer the wings were up), steering, ±8 speed clamp | Physics step |
| `WRAPX` | Bump (≤3 px per tick) and screen wrap | Physics step |
| `CKGND` | Landing on cliff tops (a lava-troll spawn is handed back) | Physics step |
| `BCKCOL` / `BCKPIX` / `BPCOL` | Pixel collision against cliff masks and the bounce response | Physics step |
| `STANDR` / `REVR` / `RUNR` / `RUNSR` / `SKIDR` | Ground state-table callbacks: run frames and steps, footstep and skid sounds | Ground step |
| `SRCADG` / `SRCADH` / `SRCADA` / `SRCADR` | Collision box (taller on the ground) and horse/rider image selection | Ground step |
| `WPLYR` / `CPLYR` / `CL1CLS` / `WR1CLS` / `CL2CLS` / `WR2CLS` | Queue draw/erase DMA blocks per screen quadrant | Ground step |
| `HITEM` | Bird-vs-bird box and pixel test (a real contact is handed to the original joust resolution) | Ground step |
| `OSTXUP` / `OSTXDN` / `OSTLR` / `OSTUTP` / `OSTXTP` / `OSTBMP` / `PTEBRD` | Joust bounce response: up/down by height, apart sideways, pterodactyl knock-back | Ground step |

| `CRPROC` / `CUPROC` / `KLPROC` | Create and kill processes | Scheduler step |
| `NAPTPC` / `NAPTIM` / `SUCIDE` | A process sleeps or ends | Scheduler step |
| `EXEC` / `EXEPS` / `EXEPRI` / `EXEPEN` / `EXESEC` | Walk the process list: primaries, then secondaries while the blitter IRQ allows | Scheduler step |
| `DTBOVR` (EXEDON) | Link the frame's per-quadrant DMA lists for the IRQ | Scheduler step |
| `SCODSP` (+ resume points) / `BCDMSN` / `BCDLSN` | Score display process as coroutine slices; draw one BCD digit | Scheduler step |
| Bird process: `PLYRLP`, `PLYRS2`, `PL2RIT`, `PL2EDG`, `STFLY`, `STFALL`, `STLAN`, `FLAPLP`/`FLIPLP` and their resume points | Ground loop (state table, stepping, walking off edges), take-off, falling, flap/flip flight loops, landing; players and enemies | Player-loop step |
| `AIROVR` / `P1JOY` / `P2JOY` | In-air overhead (facing, time in air); joystick reads through the widget PIA | Player-loop step |
| `PLYCOL` / `OPPCOL` (+ loop labels and resume points) | Collision sweeps: players vs everything, enemies vs enemies; target-timer countdown | Joust step |
| `DEATH1` / `DEATH2` (`SPDIE`, `LZAP`) / `DEATH3` / `DEATH4` | Player death (gladiator bounty, death puff, 50 points, anti-tamper check, target removal, lives, game over), enemy death (drops the egg that hatches one level smarter), pterodactyl death; run inline in a kill, so a whole kill is native | Death step |
| Egg process: `EGGLPA`, `EGGBCK`, `EGGLND`, `EGGLN2`, `EGGNXF`, `EGGLLP`, `EGGHIT`, `EGGSC2`, `SCRAIR`, `EGGDON` and their resume points | Falling and bouncing (each bounce quarters the fall, slows the drift), waiting on a ledge (falls again if it burns away), hatching when the wave has room (spawns the buzzard from the far side at the egg's level, wiggle frames, the standing man), collection with a non-overlapping floating score and the caught-in-the-air bonus | Egg step |
| `ADDEGG` / `WEGG` / `WEGPAR` / `CEGG` / `CLIPER` / `EGGBKG` / `EGAREA` | Egg helpers: gravity and the egg's 4–288 wrap, tumble frame by speed, draw/erase, clipping at the lava line, cliff refresh, score position | Egg step |
| `HITEM` contact path (`OSTHIT`, `OSTBO`, `OSTHT2`, `EGGSCR`, `OSTWIN` + resume points) | The joust: higher lance wins, equal lances bounce, enemies thud, pterodactyl only dies to a lance in its mouth while facing it, egg pickups (250/500/750/1000, +500 caught in the air), the kill and the winner's score | Joust step |
| Enemy AI (`PJOY` routines): `LINET`, `BOUNDR`, `B2UNDR`, `SHADOW`, `PTERO` (+ `PATCH4`/`5`/`6`/`9`), `SEEKE`, `AUTOFF`, `PTEOFF`, `SELPLY`, and every label they store in `PJOY` (40 entries) | Line tracking until the wave needs smarter enemies; Bounder, Hunter and Shadow Lord seeking (vertical range seeks, level flight, cliff look-ahead, flapping out of the lava, sometimes turning around instead of copying the player); the pterodactyl's line tracking and attack run; a buzzard fetching its rider; flying off screen. Ported instruction by instruction (`Asm`) because decisions hinge on exact flags and original quirks such as a stale X. Run inline from the bird process; a rider mounting (`MOUNRI`) and leaving the screen hand back | AI step |
| `IRQ` (the 4 ms interrupt handler) | Watchdog, random seed, ROM select for the blitter; on line 0 the palette copy, minutes of game time and coin-door switch debounce; then the frame's DMA table played to the blitter a quadrant at a time while the beam is elsewhere, freeing the table when all four are done. It reads the beam counter and clears the PIA flags itself, so it runs natively only when no interrupt edge lands inside it; switch presses (coin, advance, high-score reset) and the minute audit hand back | IRQ step |
| Lava troll: the spawn in `CKGND` (`LNDB7`), `ADDLAV` (its pull on a gripped bird) and its processes `LAVATC`/`LAVAT1`, `LT1HT`, `LT1GRP`, `LAV2LP`/`LAVAT2`, `LT2DIE`, `LT1DRP`, `LT1DON` (+ nap resume points, `LAVVFY`/`LAVVIC`, `PATCH1`/`PATCH2`) | A bird skimming the lava once the troll is awake starts a troll; the hand rises frame by frame under the victim, tracks its height and grips, or sinks again if the victim gets out of reach; a gripped bird's gravity becomes the troll's pull (growing after 30 s), until it breaks free (50 points) or is dragged under. Run inline from the bird process and `CKGND`; being dragged into the lava (`ADGFLR`) hands back | Troll step |
| Transporters and the lava death: `CREP1`/`CREP2`, `CREEM`, `CRELP`, `CREPLY`, `CREALL`, `FREET`/`TRBOND`, `GOTRn`/`GOTTR`, `TREFF`/`TREFF2` and the wait for the first move, `PLYINT`, `PLAYR`, `PLYLI2`, `STPLY1`/`STPLY2`, `ADGFLR`/`ADGLAV`/`ADGDED`, `FCLIP`, `BCKYUP`/`WR1ALL` (+ resume points) | The take-a-number queue (players first), a player's preference for an empty level, crowding by objects projected into each transporter's safety zone, the 30-frame fade-in with the bird growing out of the transporter, the blinking wait until the first move or time-out, joining the bird process; sinking into the lava, dying with no victor and being created again. Run inline from the bird and the scheduler; losing a life (`DECLIV`, which updates the audits) and non-native joystick or death routines leave and resume | Transporter step |
| `EXECST` / `EXEINT` (frame start) and the wave controller's in-play loop (`EMY2`/`EMYOK`) | Pick the other DMA table and wait until the IRQ has played it, hand the next queued sound code to the sound board, reset the eight quadrant erase/write lists, then walk the process list. The busy wait is skipped in whole loop iterations up to the next interrupt edge, so the interrupt still lands on the same instruction (and the skipped cycles still count as idle). Every 8 ticks the wave controller ends the wave when no riders are left, sends off baiter pterodactyls on their timetable (three at most) and every 15 s lets one more enemy get smart; the wave set-up and its messages (`IWAVE`) are still original | Frame/wave step |
| Wave set-up: `CIA`, `IWAVE` (dynamic difficulty, lava level), `IWAVE2`/`STBRID` (bridge burning, troll flames), `WNRM` (counters, BCD wave number, wave table, cliff state), `WAVEN`/`EMSGS`/`REWMSG`/`WMSG2` (wave messages, with `BCDOUT`), the `WAVDEL`/`WATWAV`/`WCREATE` coroutines, `WBEGIN` | Between waves: the difficulty adjustments step, the lava rises, the bridge burns in wave 3 and the troll's flames start, the next wave's number and messages show for a pause, the previous wave's processes finish, then the wave's Bounders, Hunters and Shadow Lords arrive one per second. The coroutines' return addresses live in the process block; a JMP through them continues natively at these resume points. Reading the difficulty setting (`RCMSA`, other ROM), the previous wave's clean-up, cliffs that change (`WCLFEW`) and the wave type routines (`WJSRTB`) leave and resume. Steps with long text output often cross an interrupt edge (text masks interrupts) and then run as the original | Wave step |
| `JZAP` loop, `KZAP` process | The anti-tamper checksums: JZAP (once per game start) sums 48 KB of ROM and RAM through the address wrap, natively in chunks that end before the next interrupt edge with the loop state left in the registers; KZAP sums one byte per tick in the background. A failed KZAP sum (`RADDR`, corrupting a random RAM byte) leaves to the original | Checksum step |
| `IRQVEC`; wave types `WINTRO`, `WCOOP`/`WAVSUR`, `WGLAD`, `WPTERO` and the end-of-wave bonuses `WCOSCR`, `WSUSCR`, `WGLSCR` (with `WAVMSG`) | The hardware IRQ vector's `JMP [$EFF8]` goes straight into the native handler. The introduction, team, survival, gladiator and pterodactyl waves show their messages (resuming inside each routine after its `WAVDEL`/`WATWAV`) and start their pterodactyls (`PTERWV`); the team and survival waves pay 3,000 points to each qualifying player. Steps end at hooked labels (`WNRM.4S`, the routines' entries) so each stays short; steps with long text output still often cross an interrupt edge and then run as the original. The egg wave (`WAVEGG`) is still original | Wave-type step |
| Egg wave: `WAVEGG` and `CREGG` (+ resume points) | The hatching enemies' kind and number from the wave table, the hatching time (halved with two players), six eggs one per ledge in random order at random places, six more at random free places, two of them hatching early; each egg process (`EGGLN2`) is created by the `CREGG` coroutine, which naps a tick and returns through `PWRTS`. Egg waves start at wave 5, so `verify-native --eggs` (`EggScenario`) steers the wave controller's table pointer so every wave is one; the ROM is untouched because its checksums are tested at boot | Egg-wave step |
| Cliff changes: the `WCLFEW` table loops (`WCLFEW.22S`/`.23S` when a cliff comes back, `CLRBIT` when one vanishes) | Each change rewrites the 352-byte landing and collision tables, 8,000–14,000 cycles per table, longer than the 4,096 between interrupts, so the loops run natively in chunks that end before the next interrupt edge (like `JZAP`). The rest of `WCLFEW` (once per cliff, it starts the cliff-destroyer process `CLFDES`) is still original. Cliffs only change from wave 81 on, so `verify-native --cliffs` (`CliffScenario`) starts each game there | Cliff step |
| Death puff: `DEATH` (+ resume points) | The puff where a rider was killed: three images over about a second, kept on screen, then a cliff it overlapped is redrawn and the process ends | Puff step |
| Game over: `GAMOVR`/`GOVWAT`/`GOVERM` and `GAMOV1` (+ resume points) | The game's end: the state becomes over, the start-switch monitor starts, every enemy and egg falls back to dumb line tracking, "THY GAME IS OVER" for 88 ticks, then the high-score check (`GAMEND`, other ROM). A player out of lives gets "PLAYER n GAME OVER" until the game ends. The credits display (`DCREDIT`, direct blitter writes) leaves and resumes; text-heavy steps often run as the original | Game-over step |
| Attract mode: `WRANIB` | One pixel of the Williams logo outline: select RAM reads, set the pixel's nibble if still blank, select ROM reads again (the most-called attract routine). The rest of attract mode (the pages' text and set-up) is still original | Attract step |
| Attract mode: logo lines, stripes, images | The marquee's line drawer (`COUNTS` pixel loop, `FILLDN` downward fill), the stripe generator (`WRPIXH`/`WRPIXV`), and the compacted-image decoder (`UNCLP` code words, `WRPIX` runs), all run in chunks that stop at a loop head before the next interrupt edge. `LINE`'s set-up and its off-screen exit (`RNGERR`) stay original | Attract step |
| Power-on self-test and memory clears | The RAM test's fill (`RAM3`) and read-back (`RAM7`), the ROM sums (`ROM1`), the start-up delay (`DELA2`), the start-up RAM clear (`INIT2.1S`) and the screen clear (`SCCLER.10S`), all in chunks that stop at the loop head before the next interrupt edge. A RAM error (`RERROR`), a ROM error and the front-door diagnostics' advance-button check stay original | Self-test step |
| Cliff refresh, pterodactyl image and cliff check | The secondary process that redraws the cliffs flagged in `BCKRFS` (`NOBCKR` and its two side-piece naps for the big cliff), and the pterodactyl's per-tick `SRCADP`/`CKPTER` (frame image, floor clamp, collision box, horse-only write `WNORIA`, end-of-wave send-off, cliff box check into `BCKCOL`) | Cliff/pterodactyl step |
| Text glyphs, phrase loop, image blocks | `OUTPUT` (one glyph, when a whole phrase didn't fit before an interrupt edge), the phrase character loop `PHROT1.1S` in chunks, and the rider/horse DMA-block fills `WRRIDR`/`WRHOR2` when reached from original code | Text step |

`verify-native` (4 seeds × 40,000 frames): about 2.3 million native calls in lockstep (identical every frame), 0 per-call mismatches. Natives handle about 46% of non-idle CPU time; the original code spends about 60% of all cycles idling until the blitter IRQ frees a DMA table. Mutation tests confirm the checks catch a single cycle, flag, memory-bit or register error, including in jump-style code.

**How the bird process is ported**
- It is one state machine over the original labels and the return addresses of its calls. Calls to native code (`WPLYR`, `CKGND`, `ADDGRA`, `ADDFLP`, `WRAPX`, the state callbacks, `SND`) run inline. A call to code that isn't native yet (attract-mode pilots through `PJOY`, the lava troll's gravity, a troll spawn) leaves the slice with the return address pushed, and the hook at that address resumes in C# when the call returns. A test checks every computed return address against the ROM bytes.
- Natives may now do PIA I/O. A transaction keeps an ordered log of I/O reads and writes and replays it at commit. The joystick read predicts the port value with `PeekWidget`, and the read is repeated at commit for its side effect of clearing the IRQ flags. The verifier snapshots and restores PIA, multiplexer and bank state around each call and compares it; lockstep compares it every frame.

**How the native scheduler works**
- It keeps the original 56-byte process blocks in RAM as its data structure, because original code still reads and edits them (the lava troll rewrites another process's `PPC`, for example).
- A native *process* is a set of C# slices keyed by its resume addresses (the `PPC` values the original stores when it naps). Each slice runs until the next nap. The scheduler, native or original, can switch between them freely, so processes move over one at a time.
- Jump-style natives (scheduler code, slices) are verified by running the original for exactly the native's cycle count and comparing the whole machine state.
- The list walk runs with interrupts enabled and reads DMA state the IRQ changes. So it advances only while no interrupt can arrive, then stops at the loop head exactly as the original would be there.

Still original code:
- `EXECST`: frame start (busy-wait for a free DMA table and sending the queued sound to the sound board).
- Process bodies: the bird's birth and transporter entry (`PLAYR`, `PLYINT`), the collision sweeps (`PLYCOL`/`OPPCOL`), transporters, lava troll, wave control, attract mode (including its `PJOY` pilots).
- The process the death routines start for game over (`GAMOVR`/`GAMOV1`). The attract mode's cycling egg score (`ATTEGG`) is handed back.
- Rare joust cases hand the whole call back: an egg score that earns an extra mount, and the "fictitious collision" `SWI`.

`verify-native --only SYM,...` checks a subset; `--trace` adds the original's instruction trace to mismatch reports (how the one `BCKPIX` bug in this step was found).

How a native routine stays exact (see `NativeRoutine`):
- It runs at the original entry address against the same registers, RAM and stack, and charges the original's cycle count including the RTS.
- It only runs when the original could not have been interrupted part-way: IRQs masked, or no interrupt-raising scanline inside its cycle window. Otherwise the original runs that call, so interrupt interleaving never changes. Routines that may hand back late (physics) compute into a transaction (`Tx`) and check this at commit, with their exact cycle count.
- It never straddles a video-frame boundary, so frame-end snapshots of native and original runs line up exactly.
- Blitter writes are allowed. The verifier journals blitter registers and blitted pixels so it can roll a native call back.
- It can hand a case back to the original by declining before touching anything.
- Callers that are still 6809 code see the exact registers and flags the original leaves. These exit contracts go away once the callers are native too.

Natives now handle about 52–54% of non-idle CPU time (4 seeds × 40,000 frames, 1.8 million native calls in lockstep, 0 mismatches).

AI step: all 40 AI entries are exercised with 0 per-call mismatches and identical lockstep over 4 seeds × 40,000 frames plus 2 two-player seeds; natives handle about 54–56% of non-idle CPU time.

IRQ step: VSCAN now follows the CPU clock (the line of the reading instruction's first cycle), so the verifier's replay of a native sees the beam where the frame loop would; normal emulation is unchanged (golden replay identical). The handler runs natively on all but about 1 in 6,000 interrupts, with 0 per-call mismatches and identical lockstep over 4 seeds × 40,000 frames plus 2 two-player seeds. Natives now handle about 82–85% of non-idle CPU time.

Troll step: the troll normally first appears in wave 4, which the random player rarely reaches, so `verify-native --troll` (and a test) wakes it from the first wave on both machines. With it: every troll label exercised, 0 per-call mismatches, lockstep identical over 4 seeds × 40,000 frames plus 2 two-player seeds; the normal runs are unchanged.

Transporter step: the lava death needs a bird in the lava, which random play never produces, so the `TrollScenario` (`verify-native --troll`) now also burns the bridge, uses a player that rarely flaps, and every 500 frames drops player 1 over the lava pit. `--probe LABEL,...` counts how often the original reaches labels. Every transporter and lava label is exercised with 0 per-call mismatches and identical lockstep (normal and troll scenarios, 4 seeds × 40,000 frames plus 2 two-player seeds). Smart App Control now also blocks the locally built tools, so `scripts/verify-docker.ps1` runs `verify-native` in the container.

Frame/wave step: 0 per-call mismatches and identical lockstep (normal and troll scenarios, 4 seeds × 40,000 frames plus 2 two-player seeds). Natives now handle about 85–87% of non-idle CPU time in normal play.

Wave step: every wave set-up label exercised, 0 per-call mismatches, lockstep identical (normal and troll scenarios). `verify-native --profile` shows where the original code still runs: about 13–15% of non-idle time, mostly the power-on self-test at boot (`RAM6`, `ROM1`, `FILLDN`, `DELA2`), the ROM checksum checks `JZAP`/`KZAP`, and attract-mode drawing; game logic left is small.

Checksum step: 0 per-call mismatches, lockstep identical; natives now handle about 87–89% of non-idle CPU time in normal play.

Next: natives now handle about 97.5% of non-idle CPU time. What's left is spread thin, each under 0.1%: the lava fill at game start (`NEXTLN`), `GSTART`, `CYCLE`, the pterodactyl's `PTESTF` frame step and others. The random test player rarely collects eggs, so egg coverage would benefit from a scripted "chase the egg" player. `verify-native --two-players` covers player 2 and player-vs-player play.

1. **Routine hooks.** Add a `NativeRoutines` table keyed by ROM address. When the PC reaches a hooked entry, the C# implementation runs against the *same* RAM, registers and stack, then returns as the original `RTS` would.
2. **Lockstep verifier.** Run two machines side by side, pure original and hooked, with identical inputs. Compare RAM, palette and CMOS every frame and report the first diverging address, frame and routine. The golden replay script grows into a library of input recordings (wave 1–10, pterodactyls, lava troll, egg waves, two-player gladiator rounds, high-score entry).
3. **Port order** (leaves first, so each step is small and verifiable):
   - utilities: random generator (`RANDSYS`), BCD scoring, colour loads, text and phrase output
   - physics: gravity, velocity tables (`FLYX`), screen wrap (`ELEFT`/`ERIGHT`), landing and background collision
   - ground-movement state machine (`STATE` tables: stand, run, skid)
   - object-to-object pixel collision and joust resolution (highest lance wins)
   - enemy AI decision blocks (Bounder, Hunter, Shadow Lord), pterodactyl, lava troll, egg hatching
   - wave control (`CIA`), dynamic difficulty tables (`DYNADJ`), transporters, cliff destruction
   - attract mode, high-score entry, audits
4. **Scheduler last.** Replace the process list with C# coroutines (`SLEEP`/`NAP` become `yield`). The secondary-process budget is timing-dependent, so native routines carry a cycle cost measured from the emulator, which keeps frame-by-frame scheduling identical.
5. **End state.** The runtime has no CPU emulation. The 6809 machine stays in the test suite as the oracle.

## Phase 3: 2026 graphics

The original only decides *what* happens. A new renderer decides how it looks:

- Read state through `GameStateReader` (and later directly from the native model). Original image pointers (`PPICH`/`PPICR`) identify the exact animation frame, so new art maps 1:1 to original frames and stays in sync with the mechanics.
- Collision keeps using the original masks, so new art can't change gameplay.
- Logic stays at 60.1 Hz. Rendering can interpolate between logic frames for high-refresh displays.
- Ideas: high-resolution sprite sheets or 3D-lit models, animated lava with heat shimmer and glow, particle effects for eggs, deaths and pterodactyl kills, parallax cavern backdrop, dynamic lighting from the lava, a widescreen frame that keeps the play field at original proportions, and a "classic" mode with a CRT shader.

## Phase 4: 2026 audio

- The game writes 6-bit sound commands (`JoustMachine.SoundCommand`; for example `$1F`/`$1E` wing flaps, `$35` coin, `$24` start). Map each command to a modern sample or synth patch, with mixing, positional panning from object X, and music stingers.
- Optional "original sound" mode: assemble `VSNDRM4.ASM` (6800 code, Walsh-function macros) and emulate the sound board.
