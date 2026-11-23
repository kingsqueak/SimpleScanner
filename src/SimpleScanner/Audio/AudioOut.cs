using System.Runtime.InteropServices;

namespace SimpleScanner.Audio;

/// <summary>
/// The computer's default speaker through the small ss_audio library (native/audio). Audio is
/// queued here as frames (one sample per channel, interleaved) and played by the sound
/// device's own thread.
/// </summary>
public sealed partial class AudioOut : IDisposable
{
    private const string Lib = "ss_audio";
    private nint _handle;

    [LibraryImport(Lib)] private static partial nint ss_audio_open(int sampleRate, int channels, int ringFrames);
    [LibraryImport(Lib)] private static unsafe partial int ss_audio_write(nint handle, float* frames, int count);
    [LibraryImport(Lib)] private static partial int ss_audio_queued(nint handle);
    [LibraryImport(Lib)] private static partial int ss_audio_underruns(nint handle);
    [LibraryImport(Lib)] private static partial void ss_audio_close(nint handle);

    public int SampleRate { get; }

    public int Channels { get; }

    public AudioOut(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _handle = ss_audio_open(sampleRate, channels, sampleRate);     // room for one second
        if (_handle == 0) throw new InvalidOperationException("Could not open the sound output device.");
    }

    /// <summary>Frames waiting to be played.</summary>
    public int Queued => _handle == 0 ? 0 : ss_audio_queued(_handle);
    public int Underruns => _handle == 0 ? 0 : ss_audio_underruns(_handle);

    /// <summary>Queue interleaved frames. Returns how many frames were taken; the rest did not fit.</summary>
    public unsafe int Write(ReadOnlySpan<float> samples)
    {
        if (_handle == 0 || samples.IsEmpty) return 0;
        fixed (float* p = samples) return ss_audio_write(_handle, p, samples.Length / Channels);
    }

    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) ss_audio_close(h);
    }
}
