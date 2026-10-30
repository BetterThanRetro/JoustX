namespace Joust.Port;

/// <summary>
/// The assembled program's symbol table, plus the facts about how the assembler encoded an
/// operand that a native routine needs in order to charge the same cycles.
/// </summary>
public sealed class Symbols(IReadOnlyDictionary<string, long> table)
{
    public ushort this[string name] =>
        table.TryGetValue(name, out long v) ? (ushort)v : throw new KeyNotFoundException($"symbol {name} not found");

    public long Value(string name) =>
        table.TryGetValue(name, out long v) ? v : throw new KeyNotFoundException($"symbol {name} not found");

    /// <summary>Extra cycles of an indexed operand with this constant offset (n,R).</summary>
    public static int IndexCost(int offset) => offset == 0 ? 0 : offset is >= -128 and <= 127 ? 1 : 4;

    /// <summary>Extra cycles of [n,R]: the offset cost plus the indirection.</summary>
    public static int IndirectCost(int offset) => IndexCost(offset) + 3;

    /// <summary>
    /// Extra cycles when a variable is reached by address: 0 for direct page ($A0xx under
    /// ASSUME DPR:$A0), 1 for extended.
    /// </summary>
    public static int AddressCost(ushort address) => (address >> 8) == 0xA0 ? 0 : 1;
}
