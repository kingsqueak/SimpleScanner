namespace SimpleScanner.Dsp;

/// <summary>
/// The stereo part of a broadcast FM signal. The discriminator output carries left plus right
/// as ordinary audio, a 19 kHz pilot tone, and left minus right on a suppressed 38 kHz carrier
/// locked to twice the pilot: (L+R) + P sin(wt) + (L-R) sin(2wt).
///
/// A phase-locked loop follows the pilot. Its doubled phase brings left minus right back to
/// audio. <see cref="Locked"/> says whether there is a pilot to follow at all.
/// </summary>
public sealed class StereoPilot
{
    private const double PilotHz = 19000, PullHz = 50;
    private const double PilotLevel = 0.09;           // of full deviation, as broadcast
    private readonly double _nominal, _minStep, _maxStep, _alpha, _beta, _smooth;
    private double _phase, _step, _inPhase;

    public StereoPilot(double sampleRate)
    {
        _nominal = 2 * Math.PI * PilotHz / sampleRate;
        _minStep = 2 * Math.PI * (PilotHz - PullHz) / sampleRate;
        _maxStep = 2 * Math.PI * (PilotHz + PullHz) / sampleRate;
        _step = _nominal;
        // A second-order loop about 30 Hz wide; the detector's gain is half the pilot level.
        double wn = 2 * Math.PI * 30, kd = PilotLevel / 2;
        _alpha = 2 * 0.707 * wn / sampleRate / kd;
        _beta = wn * wn / (sampleRate * sampleRate) / kd;
        _smooth = 1 - Math.Exp(-1 / (sampleRate * 0.05));
    }

    /// <summary>A pilot is present and the loop is on it.</summary>
    public bool Locked { get; private set; }

    public void Reset()
    {
        _phase = 0;
        _step = _nominal;
        _inPhase = 0;
        Locked = false;
    }

    /// <summary>
    /// Follows the pilot in <paramref name="composite"/> (1.0 = full deviation) and writes the
    /// composite multiplied by the regenerated 38 kHz carrier, which low-passed is left minus right.
    /// </summary>
    public void Process(ReadOnlySpan<float> composite, Span<float> difference)
    {
        double phase = _phase, step = _step, inPhase = _inPhase;
        for (int i = 0; i < composite.Length; i++)
        {
            double m = composite[i];
            (double sin, double cos) = Math.SinCos(phase);
            // With the pilot at sin(phase + d): m cos(phase) averages to (P/2) sin d, the
            // loop's error; m sin(phase) averages to (P/2) cos d, large only when locked.
            double error = m * cos;
            inPhase += _smooth * (m * sin - inPhase);
            step = Math.Clamp(step + _beta * error, _minStep, _maxStep);
            phase += step + _alpha * error;
            if (phase > Math.PI) phase -= 2 * Math.PI;
            difference[i] = (float)(m * 4 * sin * cos);         // m * 2 sin(2 phase)
        }
        _phase = phase;
        _step = step;
        _inPhase = inPhase;
        // A third of the nominal pilot to lock, a fifth to let go.
        Locked = Locked ? inPhase > PilotLevel / 2 / 5 : inPhase > PilotLevel / 2 / 3;
    }
}
