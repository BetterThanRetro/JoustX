using Joust.Emulation;
using static Joust.Emulation.Alu6809;

namespace Joust.Port.Routines;

/// <summary>
/// SND (SYSTEM.ASM): queues a sound. X points at a sound table whose first byte is a priority.
/// Priorities 128-255 always play; 0-127 are muted while the game is over. A new sound only
/// interrupts a playing one of equal or lower priority. The exec loop sends it next frame.
/// On return X has advanced past the priority byte.
/// </summary>
public sealed class SoundQueue
{
    private readonly ushort _gameOver, _soundTimer, _soundPriority, _soundPointer;

    public SoundQueue(Symbols s)
    {
        _gameOver = s["GOVER"];
        _soundTimer = s["STMR"];
        _soundPriority = s["SPRI"];
        _soundPointer = s["SNDPTR"];
    }

    /// <summary>Body of SND, excluding its final RTS.</summary>
    public void Run(Tx t)
    {
        byte priority = t.Read(t.X);
        t.X++;
        t.A = priority;
        t.CC = Logic8(t.CC, priority);
        t.Cycles += 6 + 3;                                       // LDA ,X+ / BMI

        if ((priority & 0x80) == 0)
        {
            byte gameOver = t.Read(t.Var(_gameOver));
            t.B = gameOver;
            t.CC = Logic8(t.CC, gameOver);
            t.Cycles += 4 + Symbols.AddressCost(_gameOver) + 3;  // LDB GOVER / BPL
            if ((gameOver & 0x80) == 0)
                return;                                          // game over: low-priority sounds muted
        }

        byte timer = t.Read(t.Var(_soundTimer));
        t.B = timer;
        t.CC = Logic8(t.CC, timer);
        t.Cycles += 4 + Symbols.AddressCost(_soundTimer) + 3;    // LDB STMR / BEQ
        if (timer != 0)
        {
            byte playing = t.Read(t.Var(_soundPriority));
            Sub8(ref t.CC, priority, playing);
            t.Cycles += 4 + Symbols.AddressCost(_soundPriority) + 3; // CMPA SPRI / BLO
            if (priority < playing)
                return;                                          // a more important sound is playing
        }

        t.Write(t.Var(_soundPriority), priority);
        t.Write(t.Var(_soundTimer), 1);
        t.Write16(t.Var(_soundPointer), t.X);
        t.A = 1;
        t.CC = Logic16(t.CC, t.X);                               // STX SNDPTR is the last flag writer
        t.Cycles += 4 + 2 + 4 + 5 + Symbols.AddressCost(_soundPriority)
                    + Symbols.AddressCost(_soundTimer) + Symbols.AddressCost(_soundPointer);
    }
}

public sealed class SoundRoutine : NativeRoutine
{
    private SoundQueue _sound = null!;

    public override string Symbol => "SND";
    public override int MaxCycles => 0; // checked at commit

    public override void Bind(IReadOnlyDictionary<string, long> symbols) => _sound = new SoundQueue(new Symbols(symbols));

    public override bool Execute(NativeContext c)
    {
        var t = new Tx(c);
        _sound.Run(t);
        return t.Commit();
    }
}
