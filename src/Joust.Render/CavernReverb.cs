namespace Joust.Render;

/// <summary>
/// A light stereo "cavern" reverb for the mono sound-board output (Schroeder style: four comb
/// filters and two all-passes per side, with different delays left and right for width), after
/// a one-pole low-pass like the cabinet amplifier's. The dry signal is the original sound.
/// </summary>
public sealed class CavernReverb
{
    private readonly Comb[] _combL, _combR;
    private readonly AllPass[] _apL, _apR;
    private float _lowPass, _pan;
    private readonly float _lowPassK;

    public CavernReverb(int sampleRate)
    {
        float s = sampleRate / 44100f;
        int[] combs = [1116, 1188, 1277, 1356];
        int[] passes = [556, 441];
        const int spread = 23;
        _combL = combs.Select(d => new Comb((int)(d * s))).ToArray();
        _combR = combs.Select(d => new Comb((int)((d + spread) * s))).ToArray();
        _apL = passes.Select(d => new AllPass((int)(d * s))).ToArray();
        _apR = passes.Select(d => new AllPass((int)((d + spread) * s))).ToArray();
        _lowPassK = 1f - MathF.Exp(-2f * MathF.PI * 9000f / sampleRate);
    }

    /// <summary>How much reverb is mixed in (0 = original only).</summary>
    public float Wet { get; set; } = 0.22f;

    /// <summary>Where the sound comes from, -1 (left) to 1 (right); followed smoothly.</summary>
    public float Pan { get; set; }

    /// <summary>Reverb tail length (comb feedback).</summary>
    public float Room { get; set; } = 0.78f;

    /// <summary>Mono in, interleaved stereo out (<paramref name="stereo"/> holds 2 x input samples).</summary>
    public void Process(ReadOnlySpan<float> mono, Span<float> stereo)
    {
        for (int i = 0; i < mono.Length; i++)
        {
            _lowPass += (mono[i] - _lowPass) * _lowPassK;
            float dry = _lowPass;
            float input = dry * 0.15f;
            float l = 0, r = 0;
            foreach (var c in _combL) l += c.Run(input, Room);
            foreach (var c in _combR) r += c.Run(input, Room);
            foreach (var a in _apL) l = a.Run(l);
            foreach (var a in _apR) r = a.Run(r);
            _pan += (Math.Clamp(Pan, -1f, 1f) - _pan) * 0.0005f;   // ~40 ms glide, no zipper noise
            float angle = (_pan * 0.7f + 1f) * MathF.PI / 4f;     // equal power, never fully one-sided
            stereo[2 * i] = dry * MathF.Cos(angle) * 1.41f + l * Wet;
            stereo[2 * i + 1] = dry * MathF.Sin(angle) * 1.41f + r * Wet;
        }
    }

    private sealed class Comb(int length)
    {
        private readonly float[] _buffer = new float[length];
        private int _index;
        private float _damped;

        public float Run(float input, float feedback)
        {
            float output = _buffer[_index];
            _damped = output * 0.6f + _damped * 0.4f;            // darker repeats, like rock walls
            _buffer[_index] = input + _damped * feedback;
            if (++_index == _buffer.Length) _index = 0;
            return output;
        }
    }

    private sealed class AllPass(int length)
    {
        private readonly float[] _buffer = new float[length];
        private int _index;

        public float Run(float input)
        {
            float buffered = _buffer[_index];
            float output = buffered - input;
            _buffer[_index] = input + buffered * 0.5f;
            if (++_index == _buffer.Length) _index = 0;
            return output;
        }
    }
}
