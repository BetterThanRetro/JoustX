using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// The process scheduler (SYSTEM.ASM "EXEC"). Every game object is a process: a 56-byte block
/// in a linked list starting at PDUMMY, holding the place it resumes (PPC) and how many
/// ticks it still sleeps (PNAP). Each frame the scheduler walks the list twice:
/// <list type="number">
/// <item>primary processes (PPRI = 0): each sleeping one counts down; one reaching zero runs
///       until it naps again (NAPTIM/NAPTPC), then the walk continues after it;</item>
/// <item>secondary processes (PPRI &lt;&gt; 0), but only while the IRQ has not yet taken the
///       previous frame's DMA table ([PDTBL] = 0 ends them). The walk resumes where it stopped
///       next frame (PSEC), which is why secondary work is "as time allows".</item>
/// </list>
/// Then the frame's DMA lists are linked for the IRQ (DTBOVR) and EXECST waits for a free table.
///
/// The process blocks stay in RAM in their original layout because original code still reads
/// and edits them. The list walk runs with interrupts on and reads DMA state the IRQ changes,
/// so it only runs as far as no interrupt can arrive and then stops at the loop head exactly
/// as the original would be there.
/// </summary>
public sealed class Scheduler
{
    // Process block
    private readonly int _link, _id, _priority, _nap, _pc;

    // Base page
    private readonly ushort _head, _free, _exec, _prev, _primarySecondary, _secondary, _prevTable, _currentTable;
    private readonly ushort _dmaBegin, _dmaEnd;
    private readonly int _erase, _write, _quadB, _quadC, _quadD, _tableLinks, _tableStatus;
    private readonly ushort _stack;

    // Code addresses
    private readonly ushort _exePs, _exePri, _exePen, _exeSec, _exeDon, _exeCst;

    public Scheduler(Symbols s)
    {
        _link = s["PLINK"]; _id = s["PID"]; _priority = s["PPRI"]; _nap = s["PNAP"]; _pc = s["PPC"];
        _head = s["PDUMMY"]; _free = s["PFREE"]; _exec = s["PEXEC"]; _prev = s["PPREV"];
        _primarySecondary = s["PRISEC"]; _secondary = s["PSEC"]; _prevTable = s["PDTBL"]; _currentTable = s["DTBL"];
        _dmaBegin = s["DMABEG"]; _dmaEnd = s["DMAEND"];
        _erase = s["ERASE"]; _write = s["WRITE"];
        _quadB = s["QUADB"]; _quadC = s["QUADC"]; _quadD = s["QUADD"];
        _tableLinks = s["DTBL1A"] - s["DTBL1"];
        _tableStatus = s["DTBL1"] - s["DTBL1A"];
        _stack = s["STACK"];
        _exePs = s["EXEPS"]; _exePri = s["EXEPRI"]; _exePen = s["EXEPEN"]; _exeSec = s["EXESEC"];
        _exeDon = s["EXEDON"]; _exeCst = s["EXECST"];
    }

    private static int Ix(int offset) => Symbols.IndexCost(offset);
    private static int Ext(ushort address) => Symbols.AddressCost(address);

    // ------------------------------------------------------------------ process list edits

    /// <summary>
    /// CRPROC / CUPROC: take a block off the free list and link it in after U (CRPROC: after
    /// the running process). X = start address, A = id, B = 0 primary / else secondary.
    /// Returns Y = the new block. It wakes on the next pass.
    /// </summary>
    public void Create(Tx t, bool afterRunning)
    {
        if (afterRunning)
        {
            t.U = t.Read16(t.Var(_exec));
            t.CC = Logic16(t.CC, t.U);
            t.Cycles += 5 + Ext(_exec);
        }
        byte id = t.A, priority = t.B;
        t.Push16(t.D);                                           // PSHS D
        t.Y = t.Read16(t.Var(_free));
        t.CC = Logic16(t.CC, t.Y);
        t.D = t.Read16(t.Y + _link);
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.Var(_free), t.D);                            // unlink from the free list
        t.CC = Logic16(t.CC, t.D);
        t.D = t.Read16(t.U + _link);
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.Y + _link, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.U + _link, t.Y);                             // insert after U
        t.CC = Logic16(t.CC, t.Y);
        t.A = 1;
        t.CC = Logic8(t.CC, 1);
        t.Write(t.Y + _nap, 1);                                  // wake up on the next pass
        t.CC = Logic8(t.CC, 1);
        t.Write16(t.Y + _pc, t.X);
        t.CC = Logic16(t.CC, t.X);
        t.S += 2;                                                // PULS D
        (t.A, t.B) = (id, priority);
        t.Write(t.Y + _id, id);
        t.CC = Logic8(t.CC, id);
        t.Write(t.Y + _priority, priority);
        t.CC = Logic8(t.CC, priority);
        t.Cycles += 7 + 6 + Ext(_free) + 5 + Ix(_link) + 5 + Ext(_free) + 5 + Ix(_link)
                    + 5 + Ix(_link) + 6 + Ix(_link) + 2 + 4 + Ix(_nap) + 5 + Ix(_pc) + 7
                    + 4 + Ix(_id) + 4 + Ix(_priority);
    }

    /// <summary>
    /// KLPROC: kill every process whose id matches A under mask B (except the caller, which
    /// only gets remembered as PPREV). Killed blocks go back on the free list; if the
    /// secondary walk was parked on one, it is moved to the previous block.
    /// </summary>
    public void Kill(Tx t)
    {
        byte id = t.A, mask = t.B;
        ushort savedU = t.U;
        t.Cycles += 9;                                           // PSHS D,U
        t.X = _head;
        t.CC = Logic16(t.CC, t.X);
        t.U = t.Read16(t.Var(_head));
        t.CC = Logic16(t.CC, t.U);
        t.Cycles += 3 + 5 + Ext(_head) + 3;                      // LDX / LDU PDUMMY / BEQ
        ushort exec = t.Read16(t.Var(_exec));
        while (t.U != 0)
        {
            t.B = (byte)(mask & t.Read(t.U + _id));
            t.CC = Logic8(t.CC, t.B);
            Sub8(ref t.CC, t.B, id);
            t.Cycles += 5 + 4 + Ix(_id) + 4 + 3;                 // LDB 1,S / ANDB PID,U / CMPB ,S / BNE
            if (t.B == id)
            {
                Sub16(ref t.CC, t.U, exec);
                t.Cycles += 7 + Ext(_exec) + 3;                  // CMPU PEXEC / BNE
                if (t.U != exec)
                {
                    // KLPKIL
                    Sub16(ref t.CC, t.U, t.Read16(t.Var(_secondary)));
                    t.Cycles += 7 + Ext(_secondary) + 3;
                    if (t.U == t.Read16(t.Var(_secondary)))
                    {
                        t.Write16(t.Var(_secondary), t.X);
                        t.CC = Logic16(t.CC, t.X);
                        t.Cycles += 5 + Ext(_secondary);
                    }
                    t.D = t.Read16(t.U + _link);
                    t.CC = Logic16(t.CC, t.D);
                    t.Write16(t.X + _link, t.D);                 // unlink
                    t.CC = Logic16(t.CC, t.D);
                    t.D = t.Read16(t.Var(_free));
                    t.CC = Logic16(t.CC, t.D);
                    t.Write16(t.U + _link, t.D);                 // onto the free list
                    t.CC = Logic16(t.CC, t.D);
                    t.Write16(t.Var(_free), t.U);
                    t.CC = Logic16(t.CC, t.U);
                    t.U = t.X;
                    t.Cycles += 5 + Ix(_link) + 5 + Ix(_link) + 5 + Ext(_free) + 5 + Ix(_link)
                                + 5 + Ext(_free) + 6 + 3;       // ... TFR X,U / BRA KLPNXT
                    t.U = t.Read16(t.U + _link);
                    t.CC = Logic16(t.CC, t.U);
                    t.Cycles += 5 + Ix(_link) + 3;
                    continue;
                }
                t.Write16(t.Var(_prev), t.X);
                t.CC = Logic16(t.CC, t.X);
                t.Cycles += 5 + Ext(_prev);
            }
            t.X = t.U;                                           // KLPNOM: TFR U,X
            t.U = t.Read16(t.U + _link);
            t.CC = Logic16(t.CC, t.U);
            t.Cycles += 6 + 5 + Ix(_link) + 3;
        }
        (t.A, t.B, t.U) = (id, mask, savedU);                    // PULS D,U,PC
        t.Cycles += 11 - 5;                                      // its "RTS" share is added at commit
    }

    // ------------------------------------------------------------------ sleeping and dying

    /// <summary>
    /// NAPTPC / NAPTIM: the running process sleeps A ticks and will resume at X (NAPTPC: at
    /// the address its JSR pushed). Then the walk continues.
    /// </summary>
    public ushort? Nap(Tx t, bool fromJsr)
    {
        if (fromJsr)
        {
            t.X = t.Read16(t.S);
            t.S += 2;
            t.CC = Logic16(t.CC, t.X);
            t.Cycles += 8;                                       // LDX ,S++
        }
        t.U = t.Read16(t.Var(_exec));
        t.CC = Logic16(t.CC, t.U);
        t.Write16(t.U + _pc, t.X);
        t.CC = Logic16(t.CC, t.X);
        t.Write(t.U + _nap, t.A);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 5 + Ext(_exec) + 5 + Ix(_pc) + 4 + Ix(_nap);
        return Walk(t, Label.Ps) ?? _exePs;                      // NAPTIM falls into EXEPS
    }

    /// <summary>SUCIDE: the running process ends; its block goes back on the free list.</summary>
    public ushort? Suicide(Tx t)
    {
        t.S = _stack;
        t.CC = Logic16(t.CC, t.S);
        t.U = t.Read16(t.Var(_exec));
        t.CC = Logic16(t.CC, t.U);
        t.D = t.Read16(t.U + _link);
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.Read16(_prev), t.D);                         // STD [PPREV]: unlink
        t.CC = Logic16(t.CC, t.D);
        t.D = t.Read16(t.Var(_free));
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.U + _link, t.D);
        t.CC = Logic16(t.CC, t.D);
        t.Write16(t.Var(_free), t.U);
        t.CC = Logic16(t.CC, t.U);
        t.U = t.Read16(t.Var(_prev));                            // continue after the previous one
        t.CC = Logic16(t.CC, t.U);
        t.Cycles += 4 + 5 + Ext(_exec) + 5 + Ix(_link) + 10 + 5 + Ext(_free) + 5 + Ix(_link)
                    + 5 + Ext(_free) + 5 + Ext(_prev) + 4;      // ... JMP EXEPS
        return Walk(t, Label.Ps) ?? _exePs;
    }

    // ------------------------------------------------------------------ the walk

    public enum Label { Exec, Ps, Pri, Pen, Sec }

    private const int LongestStep = 50;

    /// <summary>
    /// EXEC / EXEPS / EXEPRI / EXEPEN / EXESEC: walk the list from <paramref name="start"/>.
    /// Returns the address to continue at: a process's resume point, EXEDON when the
    /// secondary walk ends, or a loop head when the next step might be interrupted. Null if
    /// not even one step could run.
    /// </summary>
    public ushort? Walk(Tx t, Label start)
    {
        var label = start;
        int startCycles = t.Cycles;
        while (true)
        {
            if (!t.CanRun(LongestStep))
                return t.Cycles == startCycles ? null : Address(label);

            switch (label)
            {
                case Label.Exec:
                    t.U = _head;
                    t.CC = Logic16(t.CC, t.U);
                    t.S = _stack;
                    t.CC = Logic16(t.CC, t.S);
                    t.Write(t.Var(_primarySecondary), 0);
                    t.CC = Clr(t.CC);
                    t.Cycles += 3 + 4 + 6 + Ext(_primarySecondary) + 3;
                    label = Label.Pri;
                    break;

                case Label.Ps:
                    t.A = t.Read(t.Var(_primarySecondary));
                    t.CC = Logic8(t.CC, t.A);
                    t.Cycles += 4 + Ext(_primarySecondary) + 3;
                    label = t.A != 0 ? Label.Sec : Label.Pri;
                    break;

                case Label.Pri: // EXEPRI: next primary process
                {
                    ushort? run = NextProcess(t, secondary: false);
                    if (run == ushort.MaxValue) { label = Label.Pen; break; }
                    if (run == null) break;                      // keep walking
                    return run;
                }

                case Label.Pen: // EXEPEN: primaries done, start/continue the secondaries
                    t.S = _stack;
                    t.CC = Logic16(t.CC, t.S);
                    int flag = t.Var(_primarySecondary);
                    t.Write(flag, Com8(ref t.CC, t.Read(flag)));
                    t.U = t.Read16(t.Var(_secondary));
                    t.CC = Logic16(t.CC, t.U);
                    t.Cycles += 4 + 6 + Ext(_primarySecondary) + 5 + Ext(_secondary) + 3;
                    if (t.U == 0)
                    {
                        t.U = _head;
                        t.CC = Logic16(t.CC, t.U);
                        t.Cycles += 3;
                    }
                    label = Label.Sec;
                    break;

                case Label.Sec: // EXESEC: next secondary process, while time allows
                {
                    ushort? run = NextProcess(t, secondary: true);
                    if (run == ushort.MaxValue)
                    {
                        // EXESEN: park the secondary walk here for next frame
                        t.Write16(t.Var(_secondary), t.U);
                        t.CC = Logic16(t.CC, t.U);
                        t.Cycles += 5 + Ext(_secondary);
                        return _exeDon;
                    }
                    if (run == null) break;
                    return run;
                }
            }
        }
    }

    /// <summary>
    /// One step of EXEPRI/EXESEC. Returns a resume address to run a process, null to keep
    /// walking at the same label, or ushort.MaxValue when this list (pass) is finished.
    /// </summary>
    private ushort? NextProcess(Tx t, bool secondary)
    {
        t.Write16(t.Var(_prev), t.U);
        t.CC = Logic16(t.CC, t.U);
        t.Cycles += 5 + Ext(_prev);
        if (secondary)
        {
            t.A = t.Read(t.Read16(_prevTable));                  // LDA [PDTBL]
            t.CC = Logic8(t.CC, t.A);
            t.Cycles += 4 + 5 + 3;
            if (t.A == 0)
            {
                t.Cycles += 2;                                   // EXESE2: NOP; out of time
                return ushort.MaxValue;
            }
        }
        t.U = t.Read16(t.U + _link);
        t.CC = Logic16(t.CC, t.U);
        t.Cycles += 5 + Ix(_link) + 3;
        if (t.U == 0) return ushort.MaxValue;                    // end of the list
        t.A = t.Read(t.U + _priority);
        t.CC = Logic8(t.CC, t.A);
        t.Cycles += 4 + Ix(_priority) + 3;
        if ((t.A != 0) != secondary) return null;                // not this pass's kind
        int nap = t.U + _nap;
        t.Write(nap, Dec(ref t.CC, t.Read(nap)));
        t.Cycles += 6 + Ix(_nap) + 3;
        if (!Eq(t.CC))
        {
            t.Cycles += 3;                                       // BRA back to the loop
            return null;
        }
        // EXELOP: run it
        t.Write16(t.Var(_exec), t.U);
        t.CC = Logic16(t.CC, t.U);
        t.Cycles += 5 + Ext(_exec) + 3 + Symbols.IndirectCost(_pc);
        return t.Read16(t.U + _pc);
    }

    private ushort Address(Label label) => label switch
    {
        Label.Ps => _exePs,
        Label.Pri => _exePri,
        Label.Pen => _exePen,
        Label.Sec => _exeSec,
        _ => throw new InvalidOperationException("EXEC is never a stopping point"),
    };

    // ------------------------------------------------------------------ end of frame

    /// <summary>
    /// EXEDON (DTBOVR..DTBEN): link this frame's per-quadrant erase and write lists into the
    /// order the IRQ plays them (each quadrant is drawn while the beam is in another one),
    /// mark the table ready and go back to EXECST.
    /// </summary>
    public ushort? LinkDmaTables(Tx t)
    {
        ushort table = t.Read16(t.Var(_currentTable));
        t.U = table;
        t.CC = Logic16(t.CC, table);
        t.Write16(t.Var(_prevTable), table);
        t.CC = Logic16(t.CC, table);
        t.U = (ushort)(table + _tableLinks);
        t.CC = Set(t.CC, Z, t.U == 0);
        t.X = 0;
        t.CC = Logic16(t.CC, 0);
        t.Cycles += 5 + Ext(_currentTable) + 5 + Ext(_prevTable) + 4 + Ix(_tableLinks) + 3;

        ushort Begin(int list, int quad) => t.Read16(t.Var((ushort)(_dmaBegin + list + quad)));
        int EndPtr(int list, int quad) => _dmaEnd + list + quad;
        void Load(int list, int quad)
        {
            t.D = Begin(list, quad);
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 5 + Ext((ushort)(_dmaBegin + list + quad));
        }
        void Store(int offset)                                   // STD n,U
        {
            t.Write16(t.U + offset, t.D);
            t.CC = Logic16(t.CC, t.D);
            t.Cycles += 5 + Ix(offset);
        }
        void StoreAtEnd(int list, int quad, ushort value)        // STD/STX [DMAEND+...]
        {
            t.Write16(t.Read16(EndPtr(list, quad)), value);
            t.CC = Logic16(t.CC, value);
            t.Cycles += 5 + 5;
        }

        // Quadrant A erase is linked first (played while the beam is in B).
        Load(_erase, 0);
        Store(0);
        StoreAtEnd(_erase, 0, t.X);

        // DTBC1: interrupt C plays erase B then write A
        Load(_erase, _quadB);
        t.Cycles += 3;
        if (t.D == 0)
        {
            Load(_write, 0);
            Store(2);
            t.Cycles += 3;
        }
        else
        {
            Store(2);
            Load(_write, 0);
            StoreAtEnd(_erase, _quadB, t.D);
        }
        StoreAtEnd(_write, 0, t.X);

        // DTBD1: interrupt D plays erase C then write B
        Load(_erase, _quadC);
        t.Cycles += 3;
        if (t.D == 0)
        {
            Load(_write, _quadB);
            Store(4);
            t.Cycles += 3;
        }
        else
        {
            Store(4);
            Load(_write, _quadB);
            StoreAtEnd(_erase, _quadC, t.D);
        }
        StoreAtEnd(_write, _quadB, t.X);

        // DTBA1: interrupt A plays erase D, write C, write D
        Load(_erase, _quadD);
        t.Cycles += 3;
        if (t.D == 0)
        {
            Load(_write, _quadC);
            t.Cycles += 3;
            if (t.D == 0)
            {
                Load(_write, _quadD);
                Store(6);
                t.Cycles += 3;
            }
            else
            {
                Store(6);
                Load(_write, _quadD);
                StoreAtEnd(_write, _quadC, t.D);
                t.Cycles += 3;
            }
        }
        else
        {
            Store(6);
            Load(_write, _quadC);
            t.Cycles += 3;
            if (t.D == 0)
            {
                Load(_write, _quadD);
                StoreAtEnd(_erase, _quadD, t.D);
                t.Cycles += 3;
            }
            else
            {
                StoreAtEnd(_erase, _quadD, t.D);
                Load(_write, _quadD);
                StoreAtEnd(_write, _quadC, t.D);
            }
        }
        StoreAtEnd(_write, _quadD, t.X);

        // DTBEN: table ready for the IRQ
        int status = t.U + _tableStatus;
        t.Write(status, Inc(ref t.CC, t.Read(status)));
        t.Cycles += 6 + Ix(_tableStatus) + 4;                    // INC / JMP EXECST
        return _exeCst;
    }
}
