namespace SimpleScanner.Dsp;

/// <summary>A second-order IIR section (RBJ cookbook low-pass and high-pass).</summary>
public sealed class Biquad
{
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = (float)(b0 / a0);
        _b1 = (float)(b1 / a0);
        _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0);
        _a2 = (float)(a2 / a0);
    }

    public static Biquad LowPass(double sampleRate, double hz, double q)
    {
        double w = 2 * Math.PI * hz / sampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        return new((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static Biquad HighPass(double sampleRate, double hz, double q)
    {
        double w = 2 * Math.PI * hz / sampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        return new((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    /// <summary>The two sections of a fourth-order Butterworth filter.</summary>
    public static Biquad[] Butterworth4(double sampleRate, double hz, bool highPass) =>
        highPass
            ? [HighPass(sampleRate, hz, 0.54119610), HighPass(sampleRate, hz, 1.30656296)]
            : [LowPass(sampleRate, hz, 0.54119610), LowPass(sampleRate, hz, 1.30656296)];

    public void Reset() => _z1 = _z2 = 0;

    public void Process(Span<float> x)
    {
        float z1 = _z1, z2 = _z2;
        for (int i = 0; i < x.Length; i++)
        {
            float v = x[i];
            float y = _b0 * v + z1;
            z1 = _b1 * v - _a1 * y + z2;
            z2 = _b2 * v - _a2 * y;
            x[i] = y;
        }
        // Denormals cost time on some CPUs once the input goes silent.
        _z1 = MathF.Abs(z1) < 1e-20f ? 0 : z1;
        _z2 = MathF.Abs(z2) < 1e-20f ? 0 : z2;
    }
}
