using System.Runtime.InteropServices;

namespace SimpleScanner.Digital;

/// <summary>
/// The AMBE speech decoders (AMBE+2 for DMR and NXDN, the older AMBE for D-STAR), through the ss_ambe native library (native/ambe:
/// mbelib). That library is optional, because the codec is under a patent until 2028: a copy of
/// the scanner without it still follows these signals and shows who is talking, with no speech.
/// One per voice stream; use from one thread.
/// </summary>
public sealed unsafe partial class AmbeVocoder : IDisposable
{
    private const string Lib = "ss_ambe";
    public const int ParameterBits = 49, FrameSamples = 160;
    /// <summary>The error count that stands for a frame that never arrived.</summary>
    public const int Lost = 7;

    [LibraryImport(Lib)] private static partial nint ss_ambe_new();
    [LibraryImport(Lib)] private static partial void ss_ambe_free(nint a);
    [LibraryImport(Lib)] private static partial void ss_ambe_reset(nint a);
    [LibraryImport(Lib)] private static partial void ss_ambe_decode(nint a, byte* bits, int errors, short* pcm);
    [LibraryImport(Lib)] private static partial int ss_ambe_decode_dstar(nint a, byte* rows, short* pcm);

    private nint _handle;

    /// <summary>Null when the native library is not present.</summary>
    public static AmbeVocoder? TryCreate()
    {
        try
        {
            nint h = ss_ambe_new();
            return h == 0 ? null : new AmbeVocoder(h);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private AmbeVocoder(nint handle) => _handle = handle;

    /// <summary>
    /// One 20 ms frame: its 49 parameter bits, one per byte, and the bit errors its error
    /// correction found (<see cref="Lost"/> for a missing frame, which repeats the last good
    /// one for a while and then mutes). Gives 160 samples at 8 kHz.
    /// </summary>
    public void Decode(ReadOnlySpan<byte> bits, int errors, Span<short> pcm)
    {
        if (bits.Length < ParameterBits || pcm.Length < FrameSamples) throw new ArgumentException("frame size");
        fixed (byte* b = bits)
        fixed (short* p = pcm)
            ss_ambe_decode(_handle, b, errors, p);
    }

    /// <summary>
    /// One 20 ms frame of D-STAR's older AMBE codec: the 96 bits of its four code words (4 rows of
    /// 24, one per byte) before error correction. Gives 160 samples at 8 kHz; returns the bit errors found.
    /// </summary>
    public int DecodeDstar(ReadOnlySpan<byte> rows, Span<short> pcm)
    {
        if (rows.Length < 96 || pcm.Length < FrameSamples) throw new ArgumentException("frame size");
        fixed (byte* b = rows)
        fixed (short* p = pcm)
            return ss_ambe_decode_dstar(_handle, b, p);
    }

    /// <summary>Between calls: nothing of the last voice carries into the next.</summary>
    public void Reset() => ss_ambe_reset(_handle);

    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) ss_ambe_free(h);
    }
}
