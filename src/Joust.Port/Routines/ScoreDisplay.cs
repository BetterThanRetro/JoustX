using Joust.Emulation;
using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// SCODSP (JOUSTRV4.ASM): the score display process, one per player, written as coroutine
/// slices. Its state lives in its process block like the original: PRAM = the player's score
/// table (SCRPL1/SCRPL2), PRAM+2 = the digit entry being drawn.
///
/// It draws one digit per tick (each digit naps once), then every 2 ticks checks the
/// "score changed" flag and redraws from the first significant digit (leading zeros stay
/// blank). The slices are entered at the original resume addresses, so the scheduler (native
/// or original) can switch between them freely.
/// </summary>
public sealed class ScoreDisplay
{
    private readonly int _pram;
    private readonly int _firstDigitOffset;                     // SCRINT - SCRPL1
    private readonly ushort _napVector;

    /// <summary>Resume after a digit was drawn (after JSR [4,X]).</summary>
    public ushort AfterDigit { get; }

    /// <summary>Resume after the 2-tick nap at .1S (after its JSR VNAPTPC).</summary>
    public ushort AfterIdle { get; }

    public ScoreDisplay(Symbols s)
    {
        _pram = s["PRAM"];
        _firstDigitOffset = s["SCRINT"] - s["SCRPL1"];
        _napVector = s["VNAPTPC"];
        ushort idle = s["SCODSP.1S"];
        AfterIdle = (ushort)(idle + 2 + 3);                      // LDA #2 / JSR VNAPTPC
        AfterDigit = (ushort)(idle - 6);                         // LDX PRAM+2,U / LEAX 6,X / BRA .100S
    }

    private static int Ix(int offset) => Symbols.IndexCost(offset);

    /// <summary>SCODSP entry: put up a zero (start at the ones digit entry).</summary>
    public ushort Start(Tx t)
    {
        t.X = t.Read16(t.U + _pram);
        t.CC = Logic16(t.CC, t.X);
        t.X = (ushort)(t.X + _firstDigitOffset);
        t.CC = Set(t.CC, Z, t.X == 0);
        t.Cycles += 5 + Ix(_pram) + 4 + Ix(_firstDigitOffset);
        return Digit(t);
    }

    /// <summary>Resume after a digit: advance to the next 6-byte digit entry.</summary>
    public ushort NextDigit(Tx t)
    {
        t.X = t.Read16(t.U + _pram + 2);
        t.CC = Logic16(t.CC, t.X);
        t.X = (ushort)(t.X + 6);
        t.CC = Set(t.CC, Z, t.X == 0);
        t.Cycles += 5 + Ix(_pram + 2) + 5 + 3;                   // LDX / LEAX 6,X / BRA .100S
        return Digit(t);
    }

    /// <summary>
    /// .100S: draw the digit entry at X ([score byte, screen address, nibble routine]), or
    /// at the end of the list (address negative) go idle.
    /// </summary>
    private ushort Digit(Tx t)
    {
        t.Write16(t.U + _pram + 2, t.X);
        t.CC = Logic16(t.CC, t.X);
        ushort table = t.Read16(t.U + _pram);
        t.Y = table;
        t.CC = Logic16(t.CC, t.Y);
        t.U = t.Read16(t.Y + 2);                                 // blitter control & colour
        t.CC = Logic16(t.CC, t.U);
        t.Y = t.Read16(t.X + 2);                                 // screen address
        t.CC = Logic16(t.CC, t.Y);
        t.Cycles += 5 + Ix(_pram + 2) + 6 + Ix(_pram) + 6 + 7 + 3; // ... BMI
        if (Mi(t.CC))
            return Idle(t);
        t.B = t.Read(t.Read16(t.X));                             // LDB [,X]: the score byte
        t.CC = Logic8(t.CC, t.B);
        ushort routine = t.Read16(t.X + 4);                      // BCDMSN or BCDLSN
        t.Push16(AfterDigit);                                    // JSR [4,X]
        t.Cycles += 7 + 11;
        return routine;
    }

    /// <summary>.1S: PCNAP 2.</summary>
    private ushort Idle(Tx t)
    {
        t.A = 2;
        t.CC = Logic8(t.CC, 2);
        t.Push16(AfterIdle);                                     // JSR VNAPTPC
        t.Cycles += 2 + 8;
        return _napVector;
    }

    /// <summary>
    /// After the idle nap: if the score changed, clear the flag and redraw from the first
    /// significant digit (millions, 100K, 10K, 1K, 100 or 10); otherwise nap again.
    /// </summary>
    public ushort CheckChanged(Tx t)
    {
        t.X = t.Read16(t.U + _pram);
        t.CC = Logic16(t.CC, t.X);
        t.B = t.Read(t.Read16(t.X));
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 5 + Ix(_pram) + 7 + 3;                       // LDX / LDB [,X] / BEQ
        if (t.B == 0)
            return Idle(t);                                      // unchanged
        t.Write(t.Read16(t.X), 0);                               // CLR [,X]
        t.CC = Clr(t.CC);
        t.X = (ushort)(t.X + 4);                                 // millions entry
        t.CC = Set(t.CC, Z, t.X == 0);
        t.B = t.Read(t.Read16(t.X));
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 9 + 5 + 7 + 3;                               // ... BNE .100S
        if (t.B != 0)
            return Digit(t);

        t.X = (ushort)(t.X + 6);                                 // 100K & 10K byte
        t.CC = Set(t.CC, Z, t.X == 0);
        t.B = t.Read(t.Read16(t.X));
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 5 + 7 + 3;                                   // ... BEQ .2S
        if (t.B != 0)
            return HighNibble(t);

        t.X = (ushort)(t.X + 12);                                // .2S: 1K & 100 byte
        t.CC = Set(t.CC, Z, t.X == 0);
        t.B = t.Read(t.Read16(t.X));
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 5 + 7 + 3;                                   // ... BEQ .3S
        if (t.B != 0)
            return HighNibble(t);

        t.X = (ushort)(t.X + 12);                                // .3S: tens
        t.CC = Set(t.CC, Z, t.X == 0);
        t.Cycles += 5 + 3;
        return Digit(t);
    }

    /// <summary>Start at the high digit of this byte if it is non-zero, else the low one.</summary>
    private ushort HighNibble(Tx t)
    {
        t.B = (byte)(t.B & 0xF0);
        t.CC = Logic8(t.CC, t.B);
        t.Cycles += 2 + 3;                                       // ANDB #$F0 / BNE .100S
        if (t.B != 0)
            return Digit(t);
        t.X = (ushort)(t.X + 6);
        t.CC = Set(t.CC, Z, t.X == 0);
        t.Cycles += 5 + 3;                                       // LEAX 6,X / BRA .100S
        return Digit(t);
    }
}

/// <summary>
/// BCDMSN / BCDLSN: draw one BCD digit (high or low nibble of B) at screen address Y with
/// the 3x7 score font: erase the old digit in the background colour, blit the new one in
/// the colour from U, then nap one tick (JMP VNAPTPC returns to the caller next tick).
/// </summary>
public sealed class DrawDigitRoutine(bool highNibble) : NativeRoutine
{
    private ushort _fontPointer, _blitControl, _blitColor, _blitSource, _blitDest, _blitSize, _napVector;
    private byte _cliffColor;

    public override string Symbol => highNibble ? "BCDMSN" : "BCDLSN";
    public override int MaxCycles => 0;                          // checked before any write
    public override bool EndsWithRts => false;

    public override void Bind(IReadOnlyDictionary<string, long> symbols)
    {
        var s = new Symbols(symbols);
        _fontPointer = s["FONT5"];
        _blitControl = s["DMA"];
        _blitColor = s["KDMA"];
        _blitSource = s["SDMA"];
        _blitDest = s["DDMA"];
        _blitSize = s["WDMA"];
        _napVector = s["VNAPTPC"];
        _cliffColor = (byte)(s.Value("DKB") * 0x11);
    }

    private const byte EraseControl = 0x12;                      // solid colour, column stride

    public override bool Execute(NativeContext c)
    {
        var cpu = c.Cpu;
        byte cc = cpu.CC;
        byte b = cpu.B;
        int cycles = 0;
        if (highNibble)
        {
            for (int i = 0; i < 4; i++) b = Ror8(ref cc, b);
            cycles += 8;
        }
        b = (byte)(b & 0x0F);
        cc = Logic8(cc, b);
        cc = Logic8(cc, 3 * 7 + 2);
        ushort offset = (ushort)((3 * 7 + 2) * b);               // each glyph: 2 size bytes + 3x7
        cc = Set(Set(cc, Z, offset == 0), C, (offset & 0x80) != 0); // MUL
        byte savedCc = cc;                                       // PSHS CC ... PULS CC
        ushort source = (ushort)(c.Read16(c.Read16(_fontPointer)) + offset + 2);
        byte newControl = (byte)(cpu.U >> 8), newColor = (byte)cpu.U;

        cycles += 2 + 2 + 11 + 6 + 3 + 11 + 4 + 6 + 3 + 6 + 7 + 3 + 5 + 5 + 6 + 5 + 5 + 6 + 2 + 4;
        int stalls = JoustMachine.BlitStallCycles(3, 7, EraseControl) + JoustMachine.BlitStallCycles(3, 7, newControl);
        if (!c.InterruptSafe(cycles + stalls))
            return false;

        cpu.S -= 1;
        c.Write(cpu.S, savedCc);                                 // PSHS CC
        cpu.S += 1;
        c.Write16(_blitSource, source);
        c.Write16(_blitSize, 0x0307 ^ 0x0404);                   // 3 bytes x 7 rows, SC1 XOR-4
        c.Write16(_blitDest, cpu.Y);
        c.Write(_blitColor, _cliffColor);
        c.Write(_blitControl, EraseControl);                     // erase the old digit
        c.Write(_blitColor, newColor);
        c.Write(_blitControl, newControl);                       // draw the new one
        cpu.D = (ushort)((1 << 8) | newColor);                   // TFR U,D then LDA #1
        cpu.CC = Logic8(savedCc, 1);
        c.Jump(_napVector, cycles);                              // the blits charged their stalls
        return true;
    }
}
