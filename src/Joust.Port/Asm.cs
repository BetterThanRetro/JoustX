using Joust.Emulation;
using static Joust.Emulation.Alu6809;

namespace Joust.Port;

/// <summary>
/// 6809 instructions over a transaction, for code ported instruction by instruction (the enemy
/// AI, whose decisions hinge on exact flags). Each sets the registers and flags as the CPU does
/// and charges its cycles; control flow stays in C#. Branch helpers charge the branch and return
/// whether it is taken.
/// </summary>
public sealed class Asm(Tx t)
{
    public readonly Tx T = t;

    /// <summary>An operand in memory: its address and the cycles the addressing mode adds.</summary>
    public readonly record struct Ea(int Address, int Extra);

    // ------------------------------------------------------------------ operands

    public Ea U(int offset) => new(T.U + offset, Symbols.IndexCost(offset));
    public Ea X(int offset) => new(T.X + offset, Symbols.IndexCost(offset));
    public Ea Y(int offset) => new(T.Y + offset, Symbols.IndexCost(offset));
    public Ea S(int offset) => new(T.S + offset, Symbols.IndexCost(offset));

    /// <summary>A variable reached by address (direct page or extended).</summary>
    public Ea M(ushort address) => new(T.Var(address), Symbols.AddressCost(address));

    /// <summary>A variable reached with forced extended addressing (`>label`).</summary>
    public Ea E(ushort address) => new(address, 1);

    private void Cy(int n) => T.Cycles += n;

    // ------------------------------------------------------------------ loads and stores

    public void Lda(Ea e) { T.A = T.Read(e.Address); T.CC = Logic8(T.CC, T.A); Cy(4 + e.Extra); }
    public void Ldb(Ea e) { T.B = T.Read(e.Address); T.CC = Logic8(T.CC, T.B); Cy(4 + e.Extra); }
    public void Ldd(Ea e) { T.D = T.Read16(e.Address); T.CC = Logic16(T.CC, T.D); Cy(5 + e.Extra); }
    public void Ldx(Ea e) { T.X = T.Read16(e.Address); T.CC = Logic16(T.CC, T.X); Cy(5 + e.Extra); }
    public void Ldy(Ea e) { T.Y = T.Read16(e.Address); T.CC = Logic16(T.CC, T.Y); Cy(6 + e.Extra); }
    public void Lds(Ea e) { T.S = T.Read16(e.Address); T.CC = Logic16(T.CC, T.S); Cy(6 + e.Extra); }
    public void Sta(Ea e) { Put(e.Address, T.A); T.CC = Logic8(T.CC, T.A); Cy(4 + e.Extra); }
    public void Stb(Ea e) { Put(e.Address, T.B); T.CC = Logic8(T.CC, T.B); Cy(4 + e.Extra); }
    public void Std(Ea e) { T.Write16(e.Address, T.D); T.CC = Logic16(T.CC, T.D); Cy(5 + e.Extra); }
    public void Stx(Ea e) { T.Write16(e.Address, T.X); T.CC = Logic16(T.CC, T.X); Cy(5 + e.Extra); }
    public void Sts(Ea e) { T.Write16(e.Address, T.S); T.CC = Logic16(T.CC, T.S); Cy(6 + e.Extra); }

    /// <summary>
    /// A byte store: I/O registers (PIAs, bank latch, blitter, watchdog) go to the transaction's
    /// ordered I/O log, everything else (RAM, palette, CMOS) is a pending memory write.
    /// </summary>
    private void Put(int address, byte value)
    {
        address &= 0xFFFF;
        if (address is >= 0xC400 and < 0xCC00) T.IoWrite(address, value);
        else T.Write(address, value);
    }

    public void LdaI(int v) { T.A = (byte)v; T.CC = Logic8(T.CC, T.A); Cy(2); }
    public void LdbI(int v) { T.B = (byte)v; T.CC = Logic8(T.CC, T.B); Cy(2); }
    public void LddI(int v) { T.D = (ushort)v; T.CC = Logic16(T.CC, T.D); Cy(3); }
    public void LdxI(int v) { T.X = (ushort)v; T.CC = Logic16(T.CC, T.X); Cy(3); }

    // ------------------------------------------------------------------ arithmetic

    public void Cmpa(Ea e) { Sub8(ref T.CC, T.A, T.Read(e.Address)); Cy(4 + e.Extra); }
    public void Cmpb(Ea e) { Sub8(ref T.CC, T.B, T.Read(e.Address)); Cy(4 + e.Extra); }
    public void Suba(Ea e) { T.A = Sub8(ref T.CC, T.A, T.Read(e.Address)); Cy(4 + e.Extra); }
    public void Subb(Ea e) { T.B = Sub8(ref T.CC, T.B, T.Read(e.Address)); Cy(4 + e.Extra); }
    public void Anda(Ea e) { T.A &= T.Read(e.Address); T.CC = Logic8(T.CC, T.A); Cy(4 + e.Extra); }
    public void Andb(Ea e) { T.B &= T.Read(e.Address); T.CC = Logic8(T.CC, T.B); Cy(4 + e.Extra); }
    public void Ora(Ea e) { T.A |= T.Read(e.Address); T.CC = Logic8(T.CC, T.A); Cy(4 + e.Extra); }
    public void Orb(Ea e) { T.B |= T.Read(e.Address); T.CC = Logic8(T.CC, T.B); Cy(4 + e.Extra); }
    public void Cmpd(Ea e) { Sub16(ref T.CC, T.D, T.Read16(e.Address)); Cy(7 + e.Extra); }
    public void Subd(Ea e) { T.D = Sub16(ref T.CC, T.D, T.Read16(e.Address)); Cy(6 + e.Extra); }
    public void Addd(Ea e) { T.D = Add16(ref T.CC, T.D, T.Read16(e.Address)); Cy(6 + e.Extra); }

    public void CmpaI(int v) { Sub8(ref T.CC, T.A, (byte)v); Cy(2); }
    public void CmpbI(int v) { Sub8(ref T.CC, T.B, (byte)v); Cy(2); }
    public void SubbI(int v) { T.B = Sub8(ref T.CC, T.B, (byte)v); Cy(2); }
    public void AddbI(int v) { T.B = Add8(ref T.CC, T.B, (byte)v, withCarry: false); Cy(2); }
    public void AndaI(int v) { T.A &= (byte)v; T.CC = Logic8(T.CC, T.A); Cy(2); }
    public void AndbI(int v) { T.B &= (byte)v; T.CC = Logic8(T.CC, T.B); Cy(2); }
    public void OraI(int v) { T.A |= (byte)v; T.CC = Logic8(T.CC, T.A); Cy(2); }
    public void BitaI(int v) { T.CC = Logic8(T.CC, (byte)(T.A & v)); Cy(2); }
    public void AddaI(int v) { T.A = Add8(ref T.CC, T.A, (byte)v, withCarry: false); Cy(2); }
    public void CmpdI(int v) { Sub16(ref T.CC, T.D, (ushort)v); Cy(5); }
    public void SubdI(int v) { T.D = Sub16(ref T.CC, T.D, (ushort)v); Cy(4); }
    public void AdddI(int v) { T.D = Add16(ref T.CC, T.D, (ushort)v); Cy(4); }

    // ------------------------------------------------------------------ read-modify-write

    private void Modify(Ea e, Func<byte, byte> op) { Put(e.Address, op(T.Read(e.Address))); Cy(6 + e.Extra); }

    public void Dec(Ea e) => Modify(e, m => Alu6809.Dec(ref T.CC, m));
    public void Inc(Ea e) => Modify(e, m => Alu6809.Inc(ref T.CC, m));
    public void Com(Ea e) => Modify(e, m => Com8(ref T.CC, m));
    public void Asr(Ea e) => Modify(e, m => Asr8(ref T.CC, m));
    public void Ror(Ea e) => Modify(e, m => Ror8(ref T.CC, m));
    public void Clr(Ea e) { Put(e.Address, 0); T.CC = Alu6809.Clr(T.CC); Cy(6 + e.Extra); }
    public void Tst(Ea e) { T.CC = Logic8(T.CC, T.Read(e.Address)); Cy(6 + e.Extra); }

    // ------------------------------------------------------------------ inherent

    public void Clra() { T.A = 0; T.CC = Alu6809.Clr(T.CC); Cy(2); }
    public void Clrb() { T.B = 0; T.CC = Alu6809.Clr(T.CC); Cy(2); }
    public void Tsta() { T.CC = Logic8(T.CC, T.A); Cy(2); }
    public void Nega() { T.A = Neg8(ref T.CC, T.A); Cy(2); }
    public void Negb() { T.B = Neg8(ref T.CC, T.B); Cy(2); }
    public void Incb() { T.B = Alu6809.Inc(ref T.CC, T.B); Cy(2); }
    public void Coma() { T.A = Com8(ref T.CC, T.A); Cy(2); }
    public void Comb() { T.B = Com8(ref T.CC, T.B); Cy(2); }
    public void Aslb() { T.B = Asl8(ref T.CC, T.B); Cy(2); }
    public void Rola() { T.A = Rol8(ref T.CC, T.A); Cy(2); }
    public void Asra() { T.A = Asr8(ref T.CC, T.A); Cy(2); }
    public void Rorb() { T.B = Ror8(ref T.CC, T.B); Cy(2); }
    public void Nop() => Cy(2);

    /// <summary>LEAY A,Y (sets Z).</summary>
    public void LeayAY() { T.Y = (ushort)(T.Y + (sbyte)T.A); T.CC = Set(T.CC, Z, T.Y == 0); Cy(5); }

    public void Andcc(int mask) { T.CC &= (byte)mask; Cy(3); }
    public void Orcc(int mask) { T.CC |= (byte)mask; Cy(3); }

    // ------------------------------------------------------------------ stack and calls

    public void PshsB() { T.S--; T.Write(T.S, T.B); Cy(6); }
    public void PulsB() { T.B = T.Read(T.S); T.S++; Cy(6); }
    public void PulsD() { T.D = T.Read16(T.S); T.S += 2; Cy(7); }

    /// <summary>STB ,-S.</summary>
    public void StbPush() { T.S--; T.Write(T.S, T.B); T.CC = Logic8(T.CC, T.B); Cy(6); }

    /// <summary>JSR ext to a routine ported inline: the return address slot (dead once popped) and the cycles.</summary>
    public void Jsr() { T.S -= 2; Cy(8); }

    /// <summary>The RTS of a routine called with <see cref="Jsr"/>.</summary>
    public void Rts() { T.S += 2; Cy(5); }

    // ------------------------------------------------------------------ branches

    /// <summary>A short conditional branch (3 cycles either way).</summary>
    public bool Br(bool taken) { Cy(3); return taken; }

    /// <summary>A long conditional branch (5 cycles, 6 if taken).</summary>
    public bool Lbr(bool taken) { Cy(taken ? 6 : 5); return taken; }

    public void Bra() => Cy(3);
    public void Lbra() => Cy(5);

    /// <summary>JMP extended.</summary>
    public void Jmp() => Cy(4);

    // Conditions on the current flags.
    public bool Eq => Alu6809.Eq(T.CC);
    public bool Ne => !Alu6809.Eq(T.CC);
    public bool Mi => Alu6809.Mi(T.CC);
    public bool Pl => !Alu6809.Mi(T.CC);
    public bool Lo => Cs(T.CC);
    public bool Hs => !Cs(T.CC);
    public bool Hi => Alu6809.Hi(T.CC);
    public bool Ls => !Alu6809.Hi(T.CC);
    public bool Lt => Alu6809.Lt(T.CC);
    public bool Ge => !Alu6809.Lt(T.CC);
    public bool Le => Alu6809.Le(T.CC);
    public bool Gt => !Alu6809.Le(T.CC);
}
