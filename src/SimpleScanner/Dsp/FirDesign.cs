// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner.Dsp;

/// <summary>Low-pass FIR design: a windowed sinc with a Kaiser window.</summary>
public static class FirDesign
{
    /// <summary>
    /// Taps for a low-pass filter with unity gain at DC. Frequencies are in Hz at the rate
    /// the filter runs at. The response is 6 dB down at <paramref name="cutoffHz"/> and
    /// reaches <paramref name="attenDb"/> half a <paramref name="transitionHz"/> above it.
    /// The tap count is odd, so the delay is a whole number of samples.
    /// </summary>
    public static float[] LowPass(double sampleRate, double cutoffHz, double transitionHz, double attenDb = 60)
    {
        int n = (int)Math.Ceiling((attenDb - 7.95) / (14.36 * transitionHz / sampleRate)) + 1;
        if (n % 2 == 0) n++;
        n = Math.Max(n, 3);
        double beta = attenDb > 50 ? 0.1102 * (attenDb - 8.7)
            : attenDb >= 21 ? 0.5842 * Math.Pow(attenDb - 21, 0.4) + 0.07886 * (attenDb - 21)
            : 0;
        double fc = cutoffHz / sampleRate;
        var taps = new double[n];
        double mid = (n - 1) / 2.0, sum = 0, i0Beta = BesselI0(beta);
        for (int i = 0; i < n; i++)
        {
            double x = i - mid;
            double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            double r = x / mid;
            taps[i] = sinc * BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0Beta;
            sum += taps[i];
        }
        var result = new float[n];
        for (int i = 0; i < n; i++) result[i] = (float)(taps[i] / sum);
        return result;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 40; k++)
        {
            term *= half / k;
            double t2 = term * term;
            sum += t2;
            if (t2 < sum * 1e-17) break;
        }
        return sum;
    }
}
