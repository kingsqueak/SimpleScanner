using SimpleScanner.Digital;
using SimpleScanner.Dsp;

namespace SimpleScanner;

/// <summary>What a slot is doing in a scan.</summary>
internal enum SlotState
{
    /// <summary>Not part of a scan: idle, or (the first slot, outside a scan) simply the channel being received.</summary>
    Free,
    /// <summary>Just put on a candidate: its filters still hold the last channel.</summary>
    Settle,
    /// <summary>Looking whether the candidate's squelch opens.</summary>
    Look,
    /// <summary>It holds the channel: listening, or waiting through the hang time for the signal to come back.</summary>
    Listen,
    /// <summary>Let go while it could still be heard: it is kept running for the few milliseconds its fade takes.</summary>
    Leaving,
}

/// <summary>
/// One channel being received out of the radio's span: its filter and demodulator, its squelch,
/// its digital voice, where it sits, what it is doing, and its part of a scan. The receiver works
/// its slots on the DSP thread under its configuration lock.
/// </summary>
internal sealed class MonitorSlot : IDisposable
{
    public MonitorSlot(double sampleRate)
    {
        Channel = new ChannelReceiver(sampleRate);
        Channel.SetOffset(OffsetHz);
        Channel.DiscriminatorTap = d => DigitalVoice.Feed(d, Channel.DiscriminatorHzPerUnit);
    }

    public ChannelReceiver Channel { get; }
    public Squelch Squelch { get; } = new();
    // Digital voice, all on the DSP thread under _cfg (but its status and availability).
    public DigitalVoice DigitalVoice { get; } = new();

    // Where it is.
    public double FrequencyHz { get; set; } = 162_400_000;
    public double OffsetHz { get; set; } = ScanPlan.SingleOffsetHz;
    public bool Blocked { get; set; }

    // What it is doing.
    public bool SquelchOpen { get; set; }
    // Its own level meter, in dB: up at once, down slowly.
    public double Level { get; set; } = -140;
    // One block of its audio, as the channel and the digital voice leave it.
    public float[] Left { get; set; } = [];
    public float[] Right { get; set; } = [];
    // Its sound on the way to the speakers: its seat, its fade, where it is between them now, and the
    // samples the mixer keeps from one block to the next.
    public SlotSound Sound { get; } = new();

    // Its part of a scan.
    public SlotState State { get; set; }
    public int ScanIndex { get; set; } = -1;
    // While listening: the signal is gone (or has nothing to hear) and the hang time is running, counted in ScanTimer.
    public bool Quiet { get; set; }
    public double ScanTimer { get; set; }
    public double ListenTime { get; set; }
    public bool ScanSawSignal { get; set; }
    public bool ScanHeardVoice { get; set; }

    // The channel moved: what the squelch and the noise measure have seen is of the old one.
    public void ResetSquelch()
    {
        Squelch.Reset();
        Channel.ResetRoughness();
    }

    // The channel changed or its signal went: whatever the decoder was following is over.
    public void ResetDigital() => DigitalVoice.Reset();

    // The decoders belong to the DSP thread: the receiver calls this only once that thread has ended.
    public void Dispose() => DigitalVoice.Dispose();
}
