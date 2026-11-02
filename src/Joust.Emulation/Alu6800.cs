namespace Joust.Emulation;

/// <summary>
/// The 6800's flag arithmetic, shared by the emulated core (<see cref="Cpu6800"/>) and the
/// sound program translated to C#, so both compute every flag the same way.
/// </summary>
public static class Alu6800
{
    public const byte C = 0x01, V = 0x02, Z = 0x04, N = 0x08, I = 0x10, H = 0x20;

    public static void Flag(ref byte cc, byte f, bool on) => cc = on ? (byte)(cc | f) : (byte)(cc & ~f);

    public static void Nz(ref byte cc, byte r) => cc = (byte)((cc & ~(N | Z)) | ((r & 0x80) != 0 ? N : 0) | (r == 0 ? Z : 0));

    public static void Nz16(ref byte cc, ushort r) => cc = (byte)((cc & ~(N | Z)) | ((r & 0x8000) != 0 ? N : 0) | (r == 0 ? Z : 0));

    /// <summary>Loads, stores and logic: N and Z from the value, V cleared.</summary>
    public static void Logic(ref byte cc, byte r) { Nz(ref cc, r); cc &= unchecked((byte)~V); }

    public static void Logic16(ref byte cc, ushort r) { Nz16(ref cc, r); cc &= unchecked((byte)~V); }

    public static byte Add(ref byte cc, byte a, byte b, bool carry)
    {
        int cin = carry && (cc & C) != 0 ? 1 : 0;
        int r = a + b + cin;
        Flag(ref cc, H, ((a & 0x0F) + (b & 0x0F) + cin) > 0x0F);
        Flag(ref cc, C, r > 0xFF);
        Flag(ref cc, V, ((a ^ r) & (b ^ r) & 0x80) != 0);
        Nz(ref cc, (byte)r);
        return (byte)r;
    }

    public static byte Sub(ref byte cc, byte a, byte b, bool carry)
    {
        int cin = carry && (cc & C) != 0 ? 1 : 0;
        int r = a - b - cin;
        Flag(ref cc, C, r < 0);
        Flag(ref cc, V, ((a ^ b) & (a ^ r) & 0x80) != 0);
        Nz(ref cc, (byte)r);
        return (byte)r;
    }

    /// <summary>CPX: compares X with <paramref name="m"/> (N, Z, V; C unchanged, as on the 6800).</summary>
    public static void Cpx(ref byte cc, ushort x, ushort m)
    {
        int r = x - m;
        Nz16(ref cc, (ushort)r);
        Flag(ref cc, V, ((x ^ m) & (x ^ r) & 0x8000) != 0);
    }

    /// <summary>The single-operand group ($x0-$xF of rows 4-7) on <paramref name="m"/>.</summary>
    public static byte Unary(ref byte cc, int op, byte m)
    {
        byte r;
        switch (op)
        {
            case 0x0:                                            // NEG
                r = (byte)-m;
                Nz(ref cc, r); Flag(ref cc, V, r == 0x80); Flag(ref cc, C, r != 0);
                return r;
            case 0x3:                                            // COM
                r = (byte)~m;
                Logic(ref cc, r); cc |= C;
                return r;
            case 0x4:                                            // LSR
                r = (byte)(m >> 1);
                Flag(ref cc, C, (m & 1) != 0); Nz(ref cc, r); Flag(ref cc, V, (cc & C) != 0);   // V = N ^ C, N = 0
                return r;
            case 0x6:                                            // ROR
                r = (byte)((m >> 1) | ((cc & C) != 0 ? 0x80 : 0));
                Flag(ref cc, C, (m & 1) != 0); Nz(ref cc, r); ShiftV(ref cc);
                return r;
            case 0x7:                                            // ASR
                r = (byte)((m >> 1) | (m & 0x80));
                Flag(ref cc, C, (m & 1) != 0); Nz(ref cc, r); ShiftV(ref cc);
                return r;
            case 0x8:                                            // ASL
                r = (byte)(m << 1);
                Flag(ref cc, C, (m & 0x80) != 0); Nz(ref cc, r); ShiftV(ref cc);
                return r;
            case 0x9:                                            // ROL
                r = (byte)((m << 1) | ((cc & C) != 0 ? 1 : 0));
                Flag(ref cc, C, (m & 0x80) != 0); Nz(ref cc, r); ShiftV(ref cc);
                return r;
            case 0xA:                                            // DEC
                r = (byte)(m - 1);
                Nz(ref cc, r); Flag(ref cc, V, m == 0x80);
                return r;
            case 0xC:                                            // INC
                r = (byte)(m + 1);
                Nz(ref cc, r); Flag(ref cc, V, m == 0x7F);
                return r;
            case 0xD:                                            // TST
                Logic(ref cc, m); cc &= unchecked((byte)~C);
                return m;
            case 0xF:                                            // CLR
                cc = (byte)((cc & ~(N | V | C)) | Z);
                return 0;
        }
        throw new InvalidOperationException($"undefined 6800 unary operation {op:X}");
    }

    private static void ShiftV(ref byte cc) => Flag(ref cc, V, ((cc & N) != 0) ^ ((cc & C) != 0));

    /// <summary>A branch condition ($2x low nibble) on the flags.</summary>
    public static bool Branch(byte cc, int cond) => cond switch
    {
        0x0 => true,
        0x2 => (cc & (C | Z)) == 0,                              // BHI
        0x3 => (cc & (C | Z)) != 0,                              // BLS
        0x4 => (cc & C) == 0,
        0x5 => (cc & C) != 0,
        0x6 => (cc & Z) == 0,
        0x7 => (cc & Z) != 0,
        0x8 => (cc & V) == 0,
        0x9 => (cc & V) != 0,
        0xA => (cc & N) == 0,
        0xB => (cc & N) != 0,
        0xC => ((cc & N) != 0) == ((cc & V) != 0),               // BGE
        0xD => ((cc & N) != 0) != ((cc & V) != 0),               // BLT
        0xE => (cc & Z) == 0 && ((cc & N) != 0) == ((cc & V) != 0),
        _ => (cc & Z) != 0 || ((cc & N) != 0) != ((cc & V) != 0),
    };

    public static byte Daa(ref byte cc, byte a)
    {
        int correction = 0;
        bool carry = (cc & C) != 0;
        if ((cc & H) != 0 || (a & 0x0F) > 9) correction |= 0x06;
        if (carry || a > 0x99 || (a > 0x8F && (a & 0x0F) > 9)) { correction |= 0x60; carry = true; }
        int r = a + correction;
        Nz(ref cc, (byte)r);
        Flag(ref cc, C, carry || r > 0xFF);
        return (byte)r;
    }
}
