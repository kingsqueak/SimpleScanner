// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Numerics;

namespace SimpleScanner.Dsp;

/// <summary>
/// A real FIR filter that keeps one output in every <c>factor</c> inputs (factor 1 is a plain
/// filter). Blocks of any length may be fed; the filter carries its history between them.
/// </summary>
public sealed class FirDecimator
{
    private readonly float[] _taps;
    private readonly int _factor;
    private readonly int _hist;      // samples kept from one block to the next: taps - 1
    private float[] _buf;
    private int _next;               // where in the buffer the next output's window starts

    public FirDecimator(float[] taps, int factor)
    {
        _taps = taps;
        _factor = factor;
        _hist = taps.Length - 1;
        _buf = new float[_hist + 4096];
    }

    public int Factor => _factor;

    /// <summary>The most outputs a block of <paramref name="inputCount"/> samples can give.</summary>
    public int MaxOutput(int inputCount) => inputCount / _factor + 1;

    public void Reset()
    {
        Array.Clear(_buf);
        _next = 0;
    }

    /// <summary>Filters a block. Returns the number of samples written to <paramref name="output"/>.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int n = input.Length, total = _hist + n;
        if (_buf.Length < total) Array.Resize(ref _buf, total);
        input.CopyTo(_buf.AsSpan(_hist));
        int o = 0, i = _next, taps = _taps.Length;
        for (; i + taps <= total; i += _factor)
            output[o++] = Dot(_buf.AsSpan(i, taps), _taps);
        _next = i - n;
        _buf.AsSpan(n, _hist).CopyTo(_buf);
        return o;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0, w = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        for (; i + w <= a.Length; i += w)
            acc += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
        float sum = Vector.Sum(acc);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}

/// <summary>The same filter on a complex signal held as separate I and Q arrays.</summary>
public sealed class ComplexFirDecimator(float[] taps, int factor)
{
    private readonly FirDecimator _re = new(taps, factor), _im = new(taps, factor);

    public int MaxOutput(int inputCount) => _re.MaxOutput(inputCount);

    public void Reset()
    {
        _re.Reset();
        _im.Reset();
    }

    public int Process(ReadOnlySpan<float> inRe, ReadOnlySpan<float> inIm, Span<float> outRe, Span<float> outIm)
    {
        int n = _re.Process(inRe, outRe);
        _im.Process(inIm, outIm);
        return n;
    }
}
