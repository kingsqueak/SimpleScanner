namespace SimpleScanner.Digital;

/// <summary>
/// Turns the voice frames a native receiver hands out (49 AMBE+2 parameter bits, one per byte,
/// then the frame's error count; 255 there marks the end of a call) into speech at 8 kHz, and
/// holds it until it is read. Without the vocoder library the frames are dropped.
/// </summary>
internal sealed unsafe class AmbeStream : IDisposable
{
    public const int ItemBytes = 50;
    private const int End = 255;

    private readonly AmbeVocoder? _vocoder = AmbeVocoder.TryCreate();
    private readonly short[] _pcm = new short[8000 * 3];
    private readonly short[] _frame = new short[AmbeVocoder.FrameSamples];
    private int _head, _count;

    public bool Available => _vocoder is not null;

    public void Push(byte* item)
    {
        if (_vocoder is null) return;
        if (item[AmbeVocoder.ParameterBits] == End)
        {
            _vocoder.Reset();
            return;
        }
        _vocoder.Decode(new ReadOnlySpan<byte>(item, AmbeVocoder.ParameterBits), item[AmbeVocoder.ParameterBits], _frame);
        foreach (short v in _frame)
        {
            if (_count == _pcm.Length)                   // full: the oldest goes
            {
                _head = (_head + 1) % _pcm.Length;
                _count--;
            }
            _pcm[(_head + _count++) % _pcm.Length] = v;
        }
    }

    /// <summary>Speech at 8 kHz, full scale +-1. Returns the samples written.</summary>
    public int Read(Span<float> voice)
    {
        int n = Math.Min(voice.Length, _count);
        for (int i = 0; i < n; i++)
        {
            voice[i] = _pcm[_head] / 32768f;
            _head = (_head + 1) % _pcm.Length;
        }
        _count -= n;
        return n;
    }

    public void Clear()
    {
        _vocoder?.Reset();
        _head = _count = 0;
    }

    public void Dispose() => _vocoder?.Dispose();
}
