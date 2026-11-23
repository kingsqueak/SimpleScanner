namespace SimpleScanner.Dsp;

/// <summary>
/// Raises a signal's sample rate by a whole factor: zeros between the samples, then a low-pass
/// at the old Nyquist frequency. Used to bring 8 kHz decoded voice up to the 48 kHz audio rate.
/// </summary>
public sealed class Upsampler
{
    private readonly int _factor;
    private readonly FirDecimator _filter;
    private float[] _stuffed = [];

    public Upsampler(double inputRate, int factor)
    {
        _factor = factor;
        double outRate = inputRate * factor;
        var taps = FirDesign.LowPass(outRate, inputRate * 0.45, inputRate * 0.1);
        for (int i = 0; i < taps.Length; i++) taps[i] *= factor;       // make up for the zeros
        _filter = new FirDecimator(taps, 1);
    }

    public void Reset() => _filter.Reset();

    /// <summary>Writes input.Length * factor samples to <paramref name="output"/>.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int n = input.Length * _factor;
        if (_stuffed.Length < n) _stuffed = new float[n];
        Array.Clear(_stuffed, 0, n);
        for (int i = 0; i < input.Length; i++) _stuffed[i * _factor] = input[i];
        return _filter.Process(_stuffed.AsSpan(0, n), output);
    }
}
