using System.Text;

namespace Joust.Emulation;

public sealed class RoutineStats
{
    public long Calls;
    public long Declined;
    public long Mismatches;
}

/// <summary>
/// Differential checker for native routines. For every call it runs the native version, records
/// its outcome, rolls it back, then runs the original 6809 code from the same state and compares
/// registers, flags, cycle count and every memory write. The machine continues with the
/// original's result, so a verified run behaves exactly like pure emulation.
/// </summary>
public sealed class NativeVerifier
{
    private readonly JoustMachine _m;
    private readonly List<(ushort Address, byte Old)> _journal = [];

    public NativeVerifier(JoustMachine machine)
    {
        _m = machine;
        _m.NativeDispatch = Dispatch;
    }

    public Dictionary<string, RoutineStats> Stats { get; } = [];

    /// <summary>Detailed reports for the first mismatches (capped by <see cref="MaxReports"/>).</summary>
    public List<string> Reports { get; } = [];

    public int MaxReports { get; set; } = 20;

    /// <summary>Appends the original's executed PCs to the first reports (for debugging a port).</summary>
    public bool TraceOriginal { get; set; }

    private readonly List<ushort> _trace = [];

    public long TotalMismatches => Stats.Values.Sum(s => s.Mismatches);

    private bool Dispatch(NativeRoutine routine)
    {
        if (!Stats.TryGetValue(routine.Symbol, out var stats))
            Stats[routine.Symbol] = stats = new RoutineStats();
        stats.Calls++;

        var cpu = _m.Cpu;
        var entry = cpu.Save();
        var entryIo = _m.SaveIo();
        ushort returnAddress = (ushort)((_m.Read(entry.S) << 8) | _m.Read((ushort)(entry.S + 1)));

        // 1. Native run, journaled.
        _journal.Clear();
        _m.WriteJournal = _journal;
        _m.JournalSawIo = false;
        bool handled = routine.Execute(_m.NativeContext);
        _m.WriteJournal = null;
        if (!handled)
        {
            stats.Declined++;
            if (_journal.Count > 0 || cpu.Save() != entry || _m.SaveIo() != entryIo)
                Report(stats, routine, entry, "declined after changing state");
            return false; // original runs normally
        }
        var native = cpu.Save();
        var nativeWrites = FinalValues(_journal);
        var nativeIo = _m.SaveIo();

        // 2. Roll back.
        Undo(_journal);
        cpu.Restore(entry);
        _m.RestoreIo(entryIo);

        // 3. Original run from the same state, until its RTS returns to the caller.
        _journal.Clear();
        _m.WriteJournal = _journal;
        _m.JournalSawIo = false;
        int guard = 1_000_000;
        _trace.Clear();
        bool Done() => routine.EndsWithRts
            ? cpu.PC == returnAddress && cpu.S == (ushort)(entry.S + 2)
            : cpu.Cycles >= native.Cycles;
        while (!Done())
        {
            cpu.IrqLine = false; // the dispatch window guarantees no interrupt here
            if (TraceOriginal && _trace.Count < 400) _trace.Add(cpu.PC);
            cpu.Step();
            if (--guard == 0)
            {
                _m.WriteJournal = null;
                throw new InvalidOperationException($"{routine.Symbol}: original did not return (entry {entry})");
            }
        }
        _m.WriteJournal = null;
        var original = cpu.Save();
        var originalWrites = FinalValues(_journal);

        // 4. Compare.
        var diffs = new StringBuilder();
        CompareRegisters(native, original, diffs);
        if (nativeIo != _m.SaveIo())
            diffs.Append($" I/O state native={nativeIo} original={_m.SaveIo()};");
        CompareWrites(nativeWrites, originalWrites, original.S, diffs);
        if (diffs.Length > 0)
        {
            if (TraceOriginal && Reports.Count < 3)
                diffs.Append(" original trace: " + string.Join(" ", _trace.Select(pc => pc.ToString("X4"))));
            Report(stats, routine, entry, diffs.ToString());
        }
        return true;
    }

    private Dictionary<ushort, (byte Before, byte After)> FinalValues(List<(ushort Address, byte Old)> journal)
    {
        var map = new Dictionary<ushort, (byte, byte)>();
        foreach (var (a, old) in journal)
            if (!map.ContainsKey(a))
                map[a] = (old, 0);
        foreach (var a in map.Keys.ToList())
            map[a] = (map[a].Item1, _m.Peek(a));
        return map;
    }

    private void Undo(List<(ushort Address, byte Old)> journal)
    {
        for (int i = journal.Count - 1; i >= 0; i--)
            _m.Restore(journal[i].Address, journal[i].Old);
    }

    private static void CompareRegisters(CpuState n, CpuState o, StringBuilder d)
    {
        void Check<T>(string name, T a, T b, string fmt) where T : IFormattable
        {
            if (!a.Equals(b)) d.Append($" {name} native={a.ToString(fmt, null)} original={b.ToString(fmt, null)};");
        }
        Check("A", n.A, o.A, "X2");
        Check("B", n.B, o.B, "X2");
        Check("X", n.X, o.X, "X4");
        Check("Y", n.Y, o.Y, "X4");
        Check("U", n.U, o.U, "X4");
        Check("S", n.S, o.S, "X4");
        Check("DP", n.DP, o.DP, "X2");
        Check("CC", n.CC, o.CC, "X2");
        Check("PC", n.PC, o.PC, "X4");
        Check("cycles", n.Cycles, o.Cycles, "D");
    }

    /// <summary>
    /// Compares the memory each version changed. Bytes below the final stack pointer (the
    /// original's pushed return addresses and temporaries) are dead and ignored.
    /// </summary>
    private void CompareWrites(
        Dictionary<ushort, (byte Before, byte After)> native,
        Dictionary<ushort, (byte Before, byte After)> original,
        ushort finalS,
        StringBuilder d)
    {
        int shown = 0;
        foreach (var a in native.Keys.Union(original.Keys).Order())
        {
            if (a < finalS && a >= finalS - 0x100)
                continue;
            byte before = native.TryGetValue(a, out var n) ? n.Before : original[a].Before;
            byte nv = native.TryGetValue(a, out n) ? n.After : before;
            byte ov = original.TryGetValue(a, out var o) ? o.After : before;
            if (nv != ov && shown++ < 8)
                d.Append($" [{a:X4}] native={nv:X2} original={ov:X2};");
        }
    }

    private void Report(RoutineStats stats, NativeRoutine routine, CpuState entry, string detail)
    {
        stats.Mismatches++;
        if (Reports.Count < MaxReports)
            Reports.Add($"frame {_m.Frame} {routine.Symbol} entry {entry}:{detail}");
    }

    public string Summary()
    {
        var sb = new StringBuilder();
        foreach (var (name, s) in Stats.Where(k => !k.Key.StartsWith("gen:")).OrderBy(k => k.Key))
            sb.AppendLine($"  {name,-10} calls {s.Calls,8}  declined {s.Declined,6}  mismatches {s.Mismatches}");
        // The translated program's routines (named gen:LABEL), as one line.
        var gen = Stats.Where(k => k.Key.StartsWith("gen:")).Select(k => k.Value).ToList();
        if (gen.Count > 0)
            sb.AppendLine($"  translated: {gen.Count} routines, calls {gen.Sum(s => s.Calls)}  declined {gen.Sum(s => s.Declined)}  mismatches {gen.Sum(s => s.Mismatches)}");
        return sb.ToString();
    }
}
