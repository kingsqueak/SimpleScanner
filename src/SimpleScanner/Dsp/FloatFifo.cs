namespace SimpleScanner.Dsp;

/// <summary>A fixed-size queue of samples; when full, the oldest are overwritten. One thread.</summary>
public sealed class FloatFifo(int capacity)
{
    private readonly float[] _data = new float[capacity];
    private int _head;

    public int Count { get; private set; }

    public void Clear() => Count = _head = 0;

    public void Write(ReadOnlySpan<float> samples)
    {
        foreach (float v in samples)
        {
            _data[(_head + Count) % _data.Length] = v;
            if (Count < _data.Length) Count++;
            else _head = (_head + 1) % _data.Length;
        }
    }

    /// <summary>Takes up to output.Length samples; returns how many.</summary>
    public int Read(Span<float> output)
    {
        int n = Math.Min(output.Length, Count);
        for (int i = 0; i < n; i++) output[i] = _data[(_head + i) % _data.Length];
        _head = (_head + n) % _data.Length;
        Count -= n;
        return n;
    }
}
