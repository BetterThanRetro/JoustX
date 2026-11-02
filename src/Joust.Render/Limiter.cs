namespace Joust.Render;

/// <summary>
/// A look-ahead peak limiter for the final stereo mix. Loud moments (the board's full-scale
/// steps, more so with the reverb's echoes on top) would otherwise be cut off flat at the output,
/// which sounds harsh. Here the gain comes down smoothly over the 1 ms before a peak and recovers
/// over about 80 ms, so ordinary sounds pass at their normal level and loud ones are squeezed
/// instead of clipped. A soft curve above the ceiling catches what the envelope leaves.
/// The output is delayed by the look-ahead (1 ms).
/// </summary>
public sealed class Limiter
{
    private readonly float[] _delay;                             // interleaved stereo, the look-ahead
    private readonly float[] _need;                              // the gain each delayed frame needs
    private readonly int _frames;
    private int _at;
    private float _gain = 1f;
    private readonly float _attack, _release;

    /// <param name="gain">Applied before limiting (the mix's overall level).</param>
    /// <param name="ceiling">The highest output level, below 1.</param>
    public Limiter(int sampleRate, float gain = 0.8f, float ceiling = 0.95f)
    {
        Gain = gain;
        Ceiling = ceiling;
        _frames = Math.Max(1, sampleRate / 1000);
        _delay = new float[2 * _frames];
        _need = new float[_frames];
        Array.Fill(_need, 1f);
        _attack = 1f / _frames;                                  // reach any needed gain within the look-ahead
        _release = 1f - MathF.Exp(-1f / (0.08f * sampleRate));
    }

    public float Gain { get; }
    public float Ceiling { get; }

    /// <summary>Limits interleaved stereo samples in place.</summary>
    public void Process(Span<float> stereo)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            float l = stereo[i] * Gain, r = stereo[i + 1] * Gain;
            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));

            // The quietest gain needed by any frame from the one leaving now to the one arriving.
            float need = peak > Ceiling ? Ceiling / peak : 1f;
            float target = need;
            foreach (float n in _need) target = MathF.Min(target, n);
            _gain = target < _gain
                ? MathF.Max(target, _gain - _attack)             // fall to it before the peak comes out
                : _gain + (target - _gain) * _release;           // then recover slowly

            float outL = _delay[2 * _at] * _gain, outR = _delay[2 * _at + 1] * _gain;
            _delay[2 * _at] = l;
            _delay[2 * _at + 1] = r;
            _need[_at] = need;
            _at = (_at + 1) % _frames;
            stereo[i] = Soft(outL);
            stereo[i + 1] = Soft(outR);
        }
    }

    /// <summary>Unchanged up to the ceiling; above it, eased toward 1 so no sample is cut flat.</summary>
    private float Soft(float x)
    {
        float a = MathF.Abs(x);
        if (a <= Ceiling) return x;
        float room = 1f - Ceiling;
        return MathF.CopySign(Ceiling + room * MathF.Tanh((a - Ceiling) / room), x);
    }
}
