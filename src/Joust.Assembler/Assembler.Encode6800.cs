namespace Joust.Assembler;

public sealed partial class Assembler
{
    /// <summary>
    /// A 6800 instruction. Addressing modes: #imm, direct (a known address in $00-$FF), n,X
    /// (unsigned 8-bit offset) and extended.
    /// </summary>
    private void Assemble6800(string mnemonic, Op6800 info, string operand)
    {
        switch (info.Kind)
        {
            case Op6800Kind.Inherent:
                if (operand.Length != 0) throw new AsmException($"{mnemonic} takes no operand");
                Emit(info.Opcode);
                return;

            case Op6800Kind.Branch:
            {
                long target = Eval(operand, out bool unknown);
                long rel = target - (_pc + 2);
                Emit(info.Opcode);
                if (_final && !unknown && (rel < -128 || rel > 127))
                    Error($"branch out of range ({rel})");
                Emit((int)rel);
                return;
            }
        }

        bool hasDirect = info.Kind is Op6800Kind.Memory8 or Op6800Kind.Memory16 or Op6800Kind.Store;
        int dIndexed = 0x20, dExtended = 0x30;
        if (info.Kind == Op6800Kind.Modify)
        {
            dIndexed = 0x60;
            dExtended = 0x70;
        }
        else if (info.Kind == Op6800Kind.Jump)
        {
            dIndexed = 0x20;                                     // JSR n,X = $AD, JSR ext = $BD
            dExtended = 0x30;
        }

        if (operand.StartsWith('#'))
        {
            if (info.Kind is not (Op6800Kind.Memory8 or Op6800Kind.Memory16))
                throw new AsmException($"{mnemonic} cannot take an immediate operand");
            Emit(info.Opcode);
            EmitImmediate(operand[1..], info.Kind == Op6800Kind.Memory16 ? 2 : 1);
            return;
        }

        var parts = SplitOperands(operand);
        if (parts.Count == 2 && parts[1].Trim().Equals("X", StringComparison.OrdinalIgnoreCase)
            || operand.Trim().Equals("X", StringComparison.OrdinalIgnoreCase))
        {
            string off = parts.Count == 2 ? parts[0].Trim() : "";
            long n = off.Length == 0 ? 0 : Eval(off, out _);
            if (_final) CheckRange(n, 0, 255, "index offset");
            Emit(info.Opcode + dIndexed);
            Emit((int)n);
            return;
        }

        string expr = operand;
        bool forceDirect = false, forceExtended = false;
        if (expr.StartsWith('<')) { forceDirect = true; expr = expr[1..]; }
        else if (expr.StartsWith('>')) { forceExtended = true; expr = expr[1..]; }
        long v = Eval(expr, out bool unknownAddress);
        if (hasDirect && (forceDirect || (!forceExtended && !unknownAddress && v is >= 0 and <= 0xFF)))
        {
            Emit(info.Opcode + 0x10);
            Emit((int)v);
            return;
        }
        Emit(info.Opcode + dExtended);
        CheckRange(v, -32768, 65535, "address");
        Emit16((int)v);
    }
}
