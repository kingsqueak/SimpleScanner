// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner.Dsp;

/// <summary>Shifts a complex signal in frequency by multiplying it with a rotating phasor.</summary>
public sealed class Mixer
{
    private double _c = 1, _s, _dc = 1, _ds;

    /// <summary>Shift by this many Hz (negative moves a signal above centre down to it).</summary>
    public void SetShift(double shiftHz, double sampleRate)
    {
        double w = 2 * Math.PI * shiftHz / sampleRate;
        _dc = Math.Cos(w);
        _ds = Math.Sin(w);
    }

    public void Process(Span<float> re, Span<float> im)
    {
        double c = _c, s = _s, dc = _dc, ds = _ds;
        for (int i = 0; i < re.Length; i++)
        {
            float r = re[i], q = im[i];
            re[i] = (float)(r * c - q * s);
            im[i] = (float)(r * s + q * c);
            double nc = c * dc - s * ds;
            s = c * ds + s * dc;
            c = nc;
        }
        // The recurrence drifts off the unit circle by rounding; pull it back once a block.
        double mag = Math.Sqrt(c * c + s * s);
        _c = c / mag;
        _s = s / mag;
    }
}
