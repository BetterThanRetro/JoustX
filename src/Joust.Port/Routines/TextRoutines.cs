using Joust.Emulation;

namespace Joust.Port.Routines;

/// <summary>
/// CHROUT / CHR35 / PHROUT / PHR35 (MESSAGE.ASM): draw a character, or a phrase, with the
/// blitter. A = character or phrase number, B = colour (both nibbles), X = screen address
/// (high byte = column pair, low byte = row). Every register but X is preserved; X advances
/// past the text. 5x7 or 3x5 font.
/// </summary>
/// <remarks>
/// Blitter writes go through the transaction's I/O log, so the blits run (in order) at
/// commit; their stall cycles are counted in <see cref="Tx.StallCycles"/>.
/// </remarks>
public sealed class TextOut
{
    private const byte BlitCharacter = 0x1A;                     // serial->block, colour substitute, zero suppress

    private readonly ushort _font57, _font35, _phrases, _fontSave, _aSave, _blitControl, _blitColor, _blitSource, _blitDest, _blitSize;

    public TextOut(Symbols s)
    {
        _font57 = s["FONT57"];
        _font35 = s["FONT35"];
        _phrases = s["PHRASE"];
        _fontSave = s["XSAVE"];
        _aSave = s["ASAVE"];
        _blitControl = s["DMA"];
        _blitColor = s["CON"];
        _blitSource = s["ORG"];
        _blitDest = s["DES"];
        _blitSize = s["XSIZE"];
    }

    /// <summary>The whole routine, including its final PULS U,Y,D,CC,PC (which is its return).</summary>
    public void Write(Tx t, bool phrase, bool smallFont)
    {
        ushort font = smallFont ? _font35 : _font57;
        t.Cycles += 12 + 4 + 3 + 5 + 7 + 14;                     // PSHS, LDY, ORCC, STB CON, BSR, PULS
        t.IoWrite(_blitColor, t.B);
        ushort screen = t.X;
        if (!phrase)
        {
            Draw(t, font, Glyph(t, font, t.A), ref screen);
        }
        else
        {
            t.Write16(t.Var(_fontSave), font);                   // STY XSAVE
            t.Cycles += 6 + 3 + 5 + 6 + 5;                       // STY, LDU, LEAU A,U, LDU A,U, RTS
            // PHROT1: the phrase table is indexed by a signed phrase number, scaled by 2.
            sbyte n = (sbyte)t.A;
            ushort text = t.Read16(_phrases + n + n);
            while (true)
            {
                byte ch = t.Read(text++);
                t.Cycles += 4 + 6 + 8 + 8 + 3;                   // LDA ,U / LDY XSAVE / JSR OUTPUT / TST ,U+ / BPL
                Draw(t, font, Glyph(t, font, ch), ref screen);
                if ((ch & 0x80) != 0) break;                     // last character has bit 7 set
            }
        }
        t.X = screen;
    }

    /// <summary>
    /// BCDOUT (5x7 font): A = two BCD digits, shown high digit first; a digit above 10 is blank.
    /// The whole routine including its final PULS (its return); X advances past the digits.
    /// </summary>
    public void WriteBcd(Tx t)
    {
        t.Cycles += 12 + 4 + 6 + 3 + 5 + 7;                      // PSHS, LDY #FONT57, STY XSAVE, ORCC, STB CON, BSR BCDOT1
        t.IoWrite(_blitColor, t.B);
        t.Write16(t.Var(_fontSave), _font57);
        t.Write(t.Var(_aSave), t.A);
        t.Cycles += 4 + Symbols.AddressCost(_aSave) + 8 + 2 + 3; // STA ASAVE / LSRA x4 / CMPA / BLE
        ushort screen = t.X;
        foreach (int digit in new[] { t.A >> 4, t.A & 0x0F })
        {
            byte ch = (byte)digit;
            if (ch > 0x0A)
            {
                ch = 0x0A;                                       // CSPC: blank
                t.Cycles += 2;
            }
            t.Cycles += 6;                                       // LDY XSAVE
            Draw(t, _font57, Glyph(t, _font57, ch), ref screen);
        }
        t.Cycles += 8 + 4 + Symbols.AddressCost(_aSave) + 2 + 2 + 3 + 4 + 14; // JSR OUTPUT / LDA ASAVE / ANDA / CMPA / BLE / JMP OUTPUT / PULS
        t.X = screen;
    }

    private static ushort Glyph(Tx t, ushort font, byte ch) => t.Read16(font + (sbyte)(byte)(ch << 1));

    /// <summary>OUTPUT: blit one glyph at <paramref name="screen"/> and advance by its width.</summary>
    private void Draw(Tx t, ushort font, ushort glyph, ref ushort screen)
    {
        byte width = t.Read(glyph), height = t.Read(glyph + 1);
        t.IoWrite(_blitDest, (byte)(screen >> 8));
        t.IoWrite(_blitDest + 1, (byte)screen);
        t.IoWrite(_blitSize, (byte)(width ^ 4));                 // SC1 size registers are XORed with 4
        t.IoWrite(_blitSize + 1, (byte)(height ^ 4));
        ushort source = (ushort)(glyph + 2);
        t.IoWrite(_blitSource, (byte)(source >> 8));
        t.IoWrite(_blitSource + 1, (byte)source);
        t.IoWrite(_blitControl, BlitCharacter);                  // the blit runs here
        t.StallCycles += JoustMachine.BlitStallCycles(width, height, BlitCharacter);
        t.Cycles += 64;
        screen = (ushort)(screen + (width << 8));
    }
}

/// <summary>The text routines as standalone hooks.</summary>
public sealed class TextRoutine(string symbol, bool phrase, bool smallFont) : NativeRoutine
{
    private TextOut _text = null!;

    public override string Symbol => symbol;
    public override int MaxCycles => 0;                          // checked at commit

    public override void Bind(IReadOnlyDictionary<string, long> symbols) => _text = new TextOut(new Symbols(symbols));

    public override bool Execute(NativeContext c)
    {
        var t = new Tx(c);
        _text.Write(t, phrase, smallFont);
        t.Cycles -= 5;                                           // Commit adds the "RTS" part of the PULS
        return t.Commit();
    }
}
