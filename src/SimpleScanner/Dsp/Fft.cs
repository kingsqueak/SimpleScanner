// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner.Dsp;

/// <summary>A complex FFT of a fixed power-of-two size, in place on separate real and imaginary arrays.</summary>
public sealed class Fft
{
    private readonly int _n;
    private readonly int[] _reverse;
    private readonly float[] _cos, _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentException("The size must be a power of two.", nameof(size));
        _n = size;
        _reverse = new int[size];
        int bits = int.TrailingZeroCount(size);
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _reverse[i] = r;
        }
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(2 * Math.PI * i / size);
            _sin[i] = (float)-Math.Sin(2 * Math.PI * i / size);
        }
    }

    public int Size => _n;

    /// <summary>Forward transform. Bin k is the frequency k/N of the sample rate; bins above N/2 are negative frequencies.</summary>
    public void Transform(Span<float> re, Span<float> im)
    {
        for (int i = 0; i < _n; i++)
        {
            int j = _reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int half = 1; half < _n; half *= 2)
        {
            int stride = _n / (2 * half);
            for (int start = 0; start < _n; start += 2 * half)
            {
                for (int k = 0; k < half; k++)
                {
                    float c = _cos[k * stride], s = _sin[k * stride];
                    int a = start + k, b = a + half;
                    float tr = re[b] * c - im[b] * s, ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
