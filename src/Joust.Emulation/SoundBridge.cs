namespace Joust.Emulation;

/// <summary>
/// Runs the sound board alongside the main machine: each main-board frame, the board runs for
/// the same stretch of time and takes the frame's sound-port writes at their own moments.
/// </summary>
public sealed class SoundBridge
{
    private const double MainClockHz = 1_000_000.0;

    private readonly JoustMachine _machine;
    private readonly List<(byte Value, long Cycle)> _writes = [];
    private long _frameStart;
    private double _owed;                                        // fractional sound cycles carried over

    public SoundBridge(JoustMachine machine, SoundBoard board, int sampleRate)
    {
        _machine = machine;
        Board = board;
        SampleRate = sampleRate;
        machine.SoundPortWrite += (v, cycle) => _writes.Add((v, cycle));
        _frameStart = machine.Cpu.Cycles;
    }

    public SoundBoard Board { get; }
    public int SampleRate { get; }

    /// <summary>Call after each <see cref="JoustMachine.RunFrame"/>; returns the frame's samples.</summary>
    public int EndFrame(Span<float> samples)
    {
        long frameEnd = _machine.Cpu.Cycles;
        int n = 0;
        long at = _frameStart;
        foreach (var (value, cycle) in _writes)
        {
            long c = Math.Clamp(cycle, at, frameEnd);
            n += RunFor(c - at, samples[n..]);
            Board.Command(value);
            at = c;
        }
        n += RunFor(frameEnd - at, samples[n..]);
        _writes.Clear();
        _frameStart = frameEnd;
        return n;
    }

    private int RunFor(long mainCycles, Span<float> samples)
    {
        _owed += mainCycles * SoundBoard.ClockHz / MainClockHz;
        long whole = (long)_owed;
        _owed -= whole;
        return whole > 0 ? Board.Run(whole, SampleRate, samples) : 0;
    }
}
