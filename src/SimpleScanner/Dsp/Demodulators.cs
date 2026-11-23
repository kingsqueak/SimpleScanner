namespace SimpleScanner.Dsp;

/// <summary>
/// FM discriminator: the phase step from each sample to the next. The output is 1.0 at the
/// stated deviation.
/// </summary>
public sealed class FmDemod(double sampleRate, double deviationHz)
{
    private readonly float _gain = (float)(sampleRate / (2 * Math.PI * deviationHz));
    private float _pr = 1, _pi;

    public void Reset()
    {
        _pr = 1;
        _pi = 0;
    }

    public void Process(ReadOnlySpan<float> re, ReadOnlySpan<float> im, Span<float> audio)
    {
        float pr = _pr, pi = _pi;
        for (int i = 0; i < re.Length; i++)
        {
            float r = re[i], q = im[i];
            audio[i] = MathF.Atan2(q * pr - r * pi, r * pr + q * pi) * _gain;
            pr = r;
            pi = q;
        }
        _pr = pr;
        _pi = pi;
    }
}

/// <summary>
/// AM envelope detector. The carrier level is tracked and divided out, so the output is the
/// modulation itself (1.0 at 100 %) whatever the signal strength: the detector is its own AGC.
/// </summary>
public sealed class AmDemod(double sampleRate)
{
    // Fast enough to follow aircraft flutter, slow enough to leave 300 Hz speech alone.
    private readonly float _alpha = (float)(1 - Math.Exp(-1 / (sampleRate * 0.05)));
    private float _carrier;

    public void Reset() => _carrier = 0;

    public void Process(ReadOnlySpan<float> re, ReadOnlySpan<float> im, Span<float> audio)
    {
        float carrier = _carrier, alpha = _alpha;
        for (int i = 0; i < re.Length; i++)
        {
            float mag = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
            // Rise at once on a new carrier so the first syllable is not a thump.
            if (mag > 4 * carrier) carrier = mag;
            carrier += alpha * (mag - carrier);
            float m = carrier > 1e-9f ? (mag - carrier) / carrier : 0;
            audio[i] = Math.Clamp(m, -1.5f, 1.5f);
        }
        _carrier = carrier;
    }
}

/// <summary>First-order de-emphasis: flat below the corner, falling 6 dB per octave above it.</summary>
public sealed class Deemphasis(double sampleRate, double timeConstantSeconds)
{
    private readonly float _a = (float)(1 - Math.Exp(-1 / (sampleRate * timeConstantSeconds)));
    private float _y;

    public void Reset() => _y = 0;

    public void Process(Span<float> x)
    {
        float y = _y, a = _a;
        for (int i = 0; i < x.Length; i++)
        {
            y += a * (x[i] - y);
            x[i] = y;
        }
        _y = MathF.Abs(y) < 1e-20f ? 0 : y;
    }
}
