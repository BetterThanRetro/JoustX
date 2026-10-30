using Joust.Emulation;

namespace Joust.Port.Routines;

/// <summary>
/// CHROUT / CHR35 / PHROUT / PHR35 (MESSAGE.ASM): draw a character, or a phrase, with the
/// blitter. A = character or phrase number, B = colour (both nibbles), X = screen address
/// (high byte = column pair, low byte = row). Every register but X is preserved; X advances
/// past the text. 5x7 or 3x5 font.
/// </summary>
/// <remarks>
/// The original masks interrupts (ORCC #$FF) right after saving registers and restores CC
/// with its final PULS, so only that short prefix has to be interrupt-free.
/// </remarks>
public sealed class TextRoutine(string symbol, bool phrase, bool smallFont) : NativeRoutine
{
    private ushort _font, _phrases, _fontSave, _blitControl, _blitColor, _blitSource, _blitDest, _blitSize;

    public override string Symbol => symbol;

    // PSHS U,Y,D,CC (12) + LDY #font (4) [+ STY XSAVE (6)] before ORCC #$FF.
    public override int MaxCycles => phrase ? 12 + 4 + 6 + 1 : 12 + 4 + 1;

    public override void Bind(IReadOnlyDictionary<string, long> symbols)
    {
        var s = new Symbols(symbols);
        _font = s[smallFont ? "FONT35" : "FONT57"];
        _phrases = s["PHRASE"];
        _fontSave = s["XSAVE"];
        _blitControl = s["DMA"];
        _blitColor = s["CON"];
        _blitSource = s["ORG"];
        _blitDest = s["DES"];
        _blitSize = s["XSIZE"];
    }

    private const byte BlitCharacter = 0x1A;                     // serial->block, colour substitute, zero suppress

    public override bool Execute(NativeContext c)
    {
        var cpu = c.Cpu;

        // Plan first: the glyphs and the exact cost (including blitter stalls), so the call can
        // still be handed back if it would not finish inside this video frame.
        var glyphs = new List<ushort>();
        int cycles = 12 + 4 + 3 + 5 + 7 + 14;                    // PSHS, LDY, ORCC, STB CON, BSR, PULS
        if (!phrase)
        {
            glyphs.Add(Glyph(c, cpu.A));
        }
        else
        {
            cycles += 6 + 3 + 5 + 6 + 5;                         // STY XSAVE, LDU, LEAU A,U, LDU A,U, RTS
            // PHROT1: the phrase table is indexed by a signed phrase number, scaled by 2.
            sbyte n = (sbyte)cpu.A;
            ushort text = c.Read16(_phrases + n + n);
            while (true)
            {
                byte ch = c.Read(text++);
                glyphs.Add(Glyph(c, ch));
                cycles += 4 + 6 + 8 + 8 + 3;                     // LDA ,U / LDY XSAVE / JSR OUTPUT / TST ,U+ / BPL
                if ((ch & 0x80) != 0) break;                     // last character has bit 7 set
            }
        }
        cycles += 64 * glyphs.Count;                             // OUTPUT per glyph
        int stalls = glyphs.Sum(g => JoustMachine.BlitStallCycles(c.Read(g), c.Read(g + 1), BlitCharacter));
        if (!c.FitsInFrame(cycles + stalls))
            return false;

        ushort screen = cpu.X;
        c.Write(_blitColor, cpu.B);
        if (phrase)
            c.Write16(cpu.DP << 8 | (_fontSave & 0xFF), _font);   // STY XSAVE (direct page)
        foreach (ushort g in glyphs)
            Draw(c, g, ref screen);
        cpu.X = screen;
        c.Return(cycles);                                        // the blits already charged their stalls
        return true;
    }

    private ushort Glyph(NativeContext c, byte ch) => c.Read16(_font + (sbyte)(byte)(ch << 1));

    /// <summary>OUTPUT: blit one glyph at <paramref name="screen"/> and advance by its width.</summary>
    private void Draw(NativeContext c, ushort glyph, ref ushort screen)
    {
        c.Write16(_blitDest, screen);
        byte width = c.Read(glyph), height = c.Read(glyph + 1);
        c.Write(_blitSize, (byte)(width ^ 4));                   // SC1 size registers are XORed with 4
        c.Write(_blitSize + 1, (byte)(height ^ 4));
        c.Write16(_blitSource, (ushort)(glyph + 2));
        c.Write(_blitControl, BlitCharacter);                    // the blit runs (and stalls the CPU) here
        screen = (ushort)(screen + (width << 8));
    }
}
