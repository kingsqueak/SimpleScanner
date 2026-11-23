using System.Runtime.InteropServices;

namespace SimpleScanner.Audio;

/// <summary>
/// A sound output device through the small ss_audio library (native/audio). Audio is queued
/// here as frames (one sample per channel, interleaved) and played by the device's own thread.
/// </summary>
public sealed partial class AudioOut : IDisposable
{
    private const string Lib = "ss_audio";
    private nint _handle;

    [LibraryImport(Lib)] private static unsafe partial int ss_audio_devices(byte* buffer, int length);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ss_audio_open(string? deviceName, int sampleRate, int channels, int ringFrames);
    [LibraryImport(Lib)] private static unsafe partial int ss_audio_write(nint handle, float* frames, int count);
    [LibraryImport(Lib)] private static partial int ss_audio_queued(nint handle);
    [LibraryImport(Lib)] private static partial int ss_audio_underruns(nint handle);
    [LibraryImport(Lib)] private static partial void ss_audio_close(nint handle);

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>The names of the output devices present now.</summary>
    public static unsafe IReadOnlyList<string> Devices()
    {
        try
        {
            byte[] buffer = new byte[16384];
            fixed (byte* p = buffer)
            {
                ss_audio_devices(p, buffer.Length);
                string all = Marshal.PtrToStringUTF8((nint)p) ?? "";
                return all.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
        }
        catch (DllNotFoundException) { return []; }
    }

    /// <summary>Opens the named device; null, empty, or a device no longer present gives the system's default.</summary>
    public AudioOut(int sampleRate, int channels, string? device = null)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _handle = ss_audio_open(device, sampleRate, channels, sampleRate);     // room for one second
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
