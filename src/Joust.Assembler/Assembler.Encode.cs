using System.Text.RegularExpressions;

namespace Joust.Assembler;

public sealed partial class Assembler
{
    private void AssembleInstruction(string mnemonic, OpInfo info, string operand)
    {
        switch (info.Kind)
        {
            case OpKind.Inherent:
                if (operand.Length != 0) throw new AsmException($"{mnemonic} takes no operand");
                EmitOpcode(info, 0);
                return;

            case OpKind.ImmediateOnly:
                if (!operand.StartsWith('#')) throw new AsmException($"{mnemonic} needs an immediate operand");
                EmitOpcode(info, 0);
                EmitImmediate(operand[1..], 1);
                return;

            case OpKind.Branch8:
            case OpKind.Branch16:
                AssembleBranch(info, operand);
                return;

            case OpKind.Transfer:
                AssembleTransfer(operand, info);
                return;

            case OpKind.PushS:
            case OpKind.PushU:
                EmitOpcode(info, 0);
                Emit(RegisterList(operand, info.Kind == OpKind.PushS));
                return;

            case OpKind.Lea:
            {
                var ix = TryIndexed(operand) ?? throw new AsmException($"{mnemonic} needs an indexed operand");
                EmitOpcode(info, 0);
                foreach (byte b in ix) Emit(b);
                return;
            }

            case OpKind.Memory8:
            case OpKind.Memory16:
            case OpKind.Store:
            case OpKind.Modify:
                AssembleMemory(mnemonic, info, operand);
                return;
        }
        throw new AsmException($"unhandled instruction kind {info.Kind}");
    }

    private void EmitOpcode(OpInfo info, int delta)
    {
        if (info.Prefix != 0) Emit(info.Prefix);
        Emit(info.Opcode + delta);
    }

    private void EmitImmediate(string expr, int size)
    {
        long v = Eval(expr, out _);
        if (size == 1)
        {
            CheckRange(v, -128, 255, "immediate byte");
            Emit((int)v);
        }
        else
        {
            CheckRange(v, -32768, 65535, "immediate word");
            Emit16((int)v);
        }
    }

    private void AssembleMemory(string mnemonic, OpInfo info, string operand)
    {
        // Opcode deltas from the base opcode for each addressing mode.
        int dDirect = 0x10, dIndexed = 0x20, dExtended = 0x30;
        if (info.Kind == OpKind.Modify)
        {
            dDirect = 0;
            dIndexed = 0x60;
            dExtended = 0x70;
        }

        if (operand.StartsWith('#'))
        {
            if (info.Kind is OpKind.Store or OpKind.Modify)
                throw new AsmException($"{mnemonic} cannot take an immediate operand");
            EmitOpcode(info, 0);
            EmitImmediate(operand[1..], info.Kind == OpKind.Memory16 ? 2 : 1);
            return;
        }

        var indexed = TryIndexed(operand);
        if (indexed != null)
        {
            EmitOpcode(info, dIndexed);
            foreach (byte b in indexed) Emit(b);
            return;
        }

        string expr = operand;
        bool forceDirect = false, forceExtended = false;
        if (expr.StartsWith('<')) { forceDirect = true; expr = expr[1..]; }
        else if (expr.StartsWith('>')) { forceExtended = true; expr = expr[1..]; }

        long v = Eval(expr, out bool unknown);
        bool direct = forceDirect || (!forceExtended && !unknown && _dpr >= 0 && ((v >> 8) & 0xFF) == _dpr && v >= 0 && v <= 0xFFFF);
        if (direct)
        {
            EmitOpcode(info, dDirect);
            Emit((int)v);
        }
        else
        {
            EmitOpcode(info, dExtended);
            CheckRange(v, -32768, 65535, "address");
            Emit16((int)v);
        }
    }

    [GeneratedRegex(@"^(-{0,2})([XYUS])(\+{0,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex BareRegisterRegex();

    /// <summary>
    /// Encodes an indexed operand (postbyte plus offset bytes), or returns null if the operand
    /// is not indexed. Handles PLAINBASE forms where the leading comma is omitted.
    /// </summary>
    private byte[]? TryIndexed(string operand)
    {
        string s = operand.Trim();
        bool indirect = false;
        if (s.StartsWith('['))
        {
            if (!s.EndsWith(']')) throw new AsmException($"missing ']' in '{operand}'");
            indirect = true;
            s = s[1..^1].Trim();
        }

        var parts = SplitOperands(s);
        string offset, reg;
        if (parts.Count == 2)
        {
            offset = parts[0];
            reg = parts[1];
        }
        else if (parts.Count == 1 && BareRegisterRegex().IsMatch(s))
        {
            offset = "";
            reg = s;
        }
        else if (parts.Count == 1 && indirect)
        {
            // Extended indirect [addr]
            long addr = Eval(s, out _);
            CheckRange(addr, 0, 0xFFFF, "address");
            return [0x9F, (byte)(addr >> 8), (byte)addr];
        }
        else
        {
            return null;
        }

        int ind = indirect ? 0x10 : 0;
        var m = BareRegisterRegex().Match(reg.Trim());
        if (!m.Success)
        {
            if (reg.Trim().Equals("PC", StringComparison.OrdinalIgnoreCase) || reg.Trim().Equals("PCR", StringComparison.OrdinalIgnoreCase))
                throw new AsmException("PC-relative indexing is not supported");
            throw new AsmException($"bad index register '{reg}'");
        }
        string pre = m.Groups[1].Value, post = m.Groups[3].Value;
        int r = Instructions6809.IndexRegs[m.Groups[2].Value];

        if (pre.Length > 0 || post.Length > 0)
        {
            if (offset.Length != 0) throw new AsmException($"offset not allowed with auto increment in '{operand}'");
            if (pre.Length > 0 && post.Length > 0) throw new AsmException($"bad index mode '{operand}'");
            int mode = (pre, post) switch
            {
                ("", "+") => 0x80,
                ("", "++") => 0x81,
                ("-", "") => 0x82,
                _ => 0x83,
            };
            if (indirect && (mode == 0x80 || mode == 0x82))
                throw new AsmException($"single auto increment/decrement cannot be indirect: '{operand}'");
            return [(byte)(mode | r | ind)];
        }

        string off = offset.Trim();
        if (off.Length == 0)
            return [(byte)(0x84 | r | ind)];
        switch (off.ToUpperInvariant())
        {
            case "A": return [(byte)(0x86 | r | ind)];
            case "B": return [(byte)(0x85 | r | ind)];
            case "D": return [(byte)(0x8B | r | ind)];
        }

        long v = Eval(off, out bool unknown);
        if (!unknown)
        {
            if (v == 0)
                return [(byte)(0x84 | r | ind)];
            if (!indirect && v >= -16 && v <= 15)
                return [(byte)(r | (int)(v & 0x1F))];
            if (v >= -128 && v <= 127)
                return [(byte)(0x88 | r | ind), (byte)v];
        }
        CheckRange(v, -32768, 65535, "index offset");
        return [(byte)(0x89 | r | ind), (byte)(v >> 8), (byte)v];
    }

    private void AssembleBranch(OpInfo info, string operand)
    {
        long target = Eval(operand, out bool unknown);
        bool isLong = info.Kind == OpKind.Branch16;
        int size = isLong ? (info.Prefix != 0 ? 4 : 3) : 2;
        long rel = target - (_pc + size);
        EmitOpcode(info, 0);
        if (isLong)
        {
            Emit16((int)rel);
        }
        else
        {
            if (_final && !unknown && (rel < -128 || rel > 127))
                Error($"branch out of range ({rel})");
            Emit((int)rel);
        }
    }

    private void AssembleTransfer(string operand, OpInfo info)
    {
        var regs = SplitOperands(operand);
        if (regs.Count != 2) throw new AsmException($"TFR/EXG needs two registers: '{operand}'");
        if (!Instructions6809.TransferRegs.TryGetValue(regs[0], out int a) ||
            !Instructions6809.TransferRegs.TryGetValue(regs[1], out int b))
            throw new AsmException($"bad register in '{operand}'");
        EmitOpcode(info, 0);
        Emit((a << 4) | b);
    }

    private static int RegisterList(string operand, bool systemStack)
    {
        int mask = 0;
        foreach (var raw in SplitOperands(operand))
        {
            string r = raw.Trim().ToUpperInvariant();
            mask |= r switch
            {
                "CC" or "CCR" => 0x01,
                "A" => 0x02,
                "B" => 0x04,
                "D" => 0x06,
                "DP" or "DPR" => 0x08,
                "X" => 0x10,
                "Y" => 0x20,
                "U" when systemStack => 0x40,
                "S" when !systemStack => 0x40,
                "PC" => 0x80,
                _ => throw new AsmException($"bad register '{raw}' in push/pull list"),
            };
        }
        return mask;
    }
}
