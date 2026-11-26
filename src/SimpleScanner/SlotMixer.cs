using SimpleScanner.Dsp;

namespace SimpleScanner;

/// <summary>
/// One slot's sound on its way to the speakers: its seat, whether it holds a channel, its fade, where
/// it is between the speakers now, and the few samples kept from one block to the next.
///
/// <see cref="Take"/> and <see cref="Leave"/> may be called from any thread under the receiver's
/// configuration lock. <see cref="Give"/> is called on the DSP thread under that lock and notes what
/// they have set, so that the mixer, which works outside the lock, reads only what the DSP thread wrote.
/// </summary>
internal sealed class SlotSound
{
    private const float FadeStep = 1f / 240;    // a few milliseconds from silence to full volume

    // Set under the receiver's lock.
    private int _seat, _takes;
    private bool _holding;

    // As they were when the block was given, and the mixer's own: on the DSP thread only.
    private float[] _left = [], _right = [];
    private int _mixSeat, _mixTakes, _seenTakes;
    private bool _mixHolding;

    /// <summary>The seat it was last given: 0 to 3, see <see cref="SlotMixer.SeatPosition"/>.</summary>
    public int Seat => _seat;
    /// <summary>It holds a channel, through the pauses of a conversation too.</summary>
    public bool Holding => _holding;
    /// <summary>The fade: 0 is silent, the volume squared is fully open.</summary>
    public float Gain { get; private set; }
    /// <summary>Where it is between the left speaker (-1) and the right (1) now.</summary>
    public double Position { get; private set; }
    /// <summary>Samples given and not yet played.</summary>
    public int Pending { get; private set; }

    /// <summary>The slot has a channel to keep: it sits in this seat until <see cref="Leave"/>.</summary>
    public void Take(int seat)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seat);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(seat, SlotMixer.Seats);
        _seat = seat;
        _holding = true;
        _takes++;
    }

    /// <summary>The slot has let its channel go. What it is still given fades out where it is.</summary>
    public void Leave() => _holding = false;

    /// <summary>
    /// One block of the slot's audio, and whether its squelch is open. To be called for every block the
    /// slot's channel was worked on, with no samples if the block gave none: a slot that is not given a
    /// block is out of the mix, and what was left of its sound is thrown away.
    /// </summary>
    public void Give(ReadOnlySpan<float> left, ReadOnlySpan<float> right, bool open)
    {
        int count = Math.Min(left.Length, right.Length);
        if (_left.Length < Pending + count)
        {
            Array.Resize(ref _left, Pending + count + 64);
            Array.Resize(ref _right, Pending + count + 64);
        }
        left[..count].CopyTo(_left.AsSpan(Pending));
        right[..count].CopyTo(_right.AsSpan(Pending));
        Pending += count;
        Given = true;
        Open = open;
        _mixSeat = _seat;
        _mixHolding = _holding;
        _mixTakes = _takes;
    }

    // ---- for the mixer, on the DSP thread ----

    internal bool Given { get; private set; }
    internal bool Open { get; private set; }
    internal bool MixHolding => _mixHolding;
    internal int MixSeat => _mixSeat;
    /// <summary>Its samples are being played in step with the others': they wait for it, and it for them.</summary>
    internal bool InStep { get; set; }

    /// <summary>It was not given this block: it is out of the mix.</summary>
    internal void Drop()
    {
        Pending = 0;
        InStep = false;
        Gain = 0;
    }

    /// <summary>
    /// Its next samples, at most <paramref name="n"/> of them, faded towards <paramref name="target"/> and
    /// placed, moving towards <paramref name="place"/>, into the stereo frames: added to what is there, or
    /// (the first slot of the block) written over it.
    /// </summary>
    internal void MixInto(Span<float> frames, int n, bool add, float target, double place)
    {
        int m = Math.Min(Pending, n);
        // A slot that has just taken a channel starts where it belongs. (If it can still be heard from
        // its last one, it glides there like any other.)
        if (_mixTakes != _seenTakes)
        {
            _seenTakes = _mixTakes;
            if (Gain == 0) Position = place;
        }
        // From the centre to its seat, or back, in the glide time.
        double glide = Math.Abs(SlotMixer.SeatPosition(_mixSeat)) / (SlotMixer.GlideSeconds * ChannelReceiver.AudioRate);
        float g = Gain;
        double p = Position;
        Given = false;

        if (add && g == 0 && target == 0)
        {
            // Silent, and staying so: only its place moves.
            p = p < place ? Math.Min(p + m * glide, place) : Math.Max(p - m * glide, place);
        }
        else if (p == place && p == 0)
        {
            // In the centre: left and right as they are.
            for (int k = 0; k < m; k++)
            {
                g += Math.Clamp(target - g, -FadeStep, FadeStep);
                float l = _left[k] * g, r = _right[k] * g;
                if (add)
                {
                    frames[2 * k] += l;
                    frames[2 * k + 1] += r;
                }
                else
                {
                    frames[2 * k] = l;
                    frames[2 * k + 1] = r;
                }
            }
        }
        else
        {
            var (gl, gr, mix) = Receiver.PanGains(p);
            for (int k = 0; k < m; k++)
            {
                g += Math.Clamp(target - g, -FadeStep, FadeStep);
                if (p != place)
                {
                    // Gliding: the gains move a little with every sample, so that nothing is heard of it.
                    p = p < place ? Math.Min(p + glide, place) : Math.Max(p - glide, place);
                    (gl, gr, mix) = Receiver.PanGains(p);
                }
                float l = _left[k], r = _right[k], both = mix * 0.5f * (l + r);
                l = ((1 - mix) * l + both) * g * gl;
                r = ((1 - mix) * r + both) * g * gr;
                if (add)
                {
                    frames[2 * k] += l;
                    frames[2 * k + 1] += r;
                }
                else
                {
                    frames[2 * k] = l;
                    frames[2 * k + 1] = r;
                }
            }
        }
        if (!add) frames[(2 * m)..(2 * n)].Clear();

        Gain = g;
        Position = p;
        Pending -= m;
        if (Pending > 0)
        {
            _left.AsSpan(m, Pending).CopyTo(_left);
            _right.AsSpan(m, Pending).CopyTo(_right);
        }
    }
}

/// <summary>
/// Puts the sound of up to four slots together for the speakers. Each slot's audio goes through its own
/// fade (which carries the volume) to its own place between the speakers, the slots are added, a limiter
/// keeps the sum within full scale, and the radio's pan is applied to the result.
///
/// A slot that holds a channel has a seat: 33% left, 33% right, hard left, hard right, in the order the
/// seats are given out. While it is the only one holding a channel it plays in the centre, and it glides
/// to its seat when another takes a channel, and back when it is alone again.
///
/// Two channels' filters do not give their audio in step sample for sample: of one block of the radio's
/// samples one slot may get a sample more than another. So what comes out of a block is as many samples
/// as every playing slot has, and a slot with more keeps the rest for the next block. With one slot that
/// is the whole block, with no delay. All of it on the DSP thread.
/// </summary>
internal sealed class SlotMixer
{
    /// <summary>The number of seats, and so of slots that can be heard apart.</summary>
    public const int Seats = 4;
    /// <summary>How long a slot takes from the centre to its seat, or back.</summary>
    public const double GlideSeconds = 0.15;
    /// <summary>
    /// The most samples one slot is made to keep while it waits for another. Channels of one radio differ by a
    /// sample or two; a slot that holds the others back by more than this has stopped giving, and they go on without it.
    /// </summary>
    public const int CarryMost = ChannelReceiver.AudioRate / 1000;
    /// <summary>After the limiter has turned the sum down: how long it stays there before it starts back.</summary>
    public const double LimiterHoldSeconds = 0.02;
    /// <summary>How long the limiter's gain takes from nothing back to full; from less far, less long.</summary>
    public const double LimiterReleaseSeconds = 0.2;

    private const int HoldSamples = (int)(LimiterHoldSeconds * ChannelReceiver.AudioRate);
    private const float Release = (float)(1 / (LimiterReleaseSeconds * ChannelReceiver.AudioRate));
    private static readonly double[] SeatPositions = [-0.33, 0.33, -1, 1];

    private float[] _frames = [];
    private float _limit = 1;
    private int _hold;

    /// <summary>Where a seat is between the left speaker (-1) and the right (1).</summary>
    public static double SeatPosition(int seat) => SeatPositions[seat];

    /// <summary>The squelch of at least one slot in the last block mixed was open.</summary>
    public bool AnyOpen { get; private set; }

    /// <summary>
    /// The slots' audio given since the last call, as stereo frames (left and right interleaved) at the
    /// volume and the radio's pan. The frames are the mixer's own and are good until the next call.
    /// </summary>
    public Span<float> Mix(ReadOnlySpan<SlotSound> slots, double volume, double pan)
    {
        int holding = 0;
        bool anyOpen = false;
        foreach (var s in slots)
        {
            if (!s.Given)
            {
                s.Drop();
                continue;
            }
            if (s.Pending > 0) s.InStep = true;
            if (s.MixHolding) holding++;
            anyOpen |= s.Open;
        }
        AnyOpen = anyOpen;

        // As many samples as every slot in step has. A slot that has only now been given a channel and
        // has no audio yet is not in step, and holds nobody up.
        int n;
        while (true)
        {
            int most = 0;
            n = int.MaxValue;
            foreach (var s in slots)
            {
                if (!s.Given) continue;
                most = Math.Max(most, s.Pending);
                if (s.InStep) n = Math.Min(n, s.Pending);
            }
            if (n == int.MaxValue) n = 0;
            if (most - n <= CarryMost) break;
            foreach (var s in slots)
                if (s.Given && s.InStep && s.Pending == n) s.InStep = false;
        }

        if (_frames.Length < 2 * n) _frames = new float[2 * n];
        var frames = _frames.AsSpan(0, 2 * n);
        // The volume is in the fade, so that a change of volume glides too.
        float target = (float)(volume * volume);
        bool add = false;
        foreach (var s in slots)
        {
            if (!s.Given) continue;
            double place = !s.MixHolding ? s.Position : holding == 1 ? 0 : SeatPosition(s.MixSeat);
            s.MixInto(frames, n, add, s.Open ? target : 0f, place);
            add = true;
        }
        if (!add) frames.Clear();

        Limit(frames);
        Pan(frames, (float)pan);
        return frames;
    }

    // A gain on the sum, the same for left and right: 1 and out of the way while the sum is within full
    // scale, down at once to what a larger sample needs, held there a moment, then back up steadily.
    private void Limit(Span<float> frames)
    {
        float g = _limit;
        int hold = _hold;
        for (int k = 0; k + 1 < frames.Length; k += 2)
        {
            float l = frames[k], r = frames[k + 1], peak = Math.Max(Math.Abs(l), Math.Abs(r));
            if (g < 1)
            {
                if (hold > 0) hold--;
                else g = Math.Min(1f, g + Release);
            }
            if (peak * g > 1)
            {
                g = 1 / peak;
                hold = HoldSamples;
            }
            if (g == 1) continue;       // within full scale: not touched
            frames[k] = Math.Clamp(l * g, -1f, 1f);
            frames[k + 1] = Math.Clamp(r * g, -1f, 1f);
        }
        _limit = g;
        _hold = hold;
    }

    // The radio's own pan, on everything it plays.
    private static void Pan(Span<float> frames, float pan)
    {
        if (pan == 0) return;
        var (gl, gr, mix) = Receiver.PanGains(pan);
        for (int k = 0; k + 1 < frames.Length; k += 2)
        {
            float l = frames[k], r = frames[k + 1], both = mix * 0.5f * (l + r);
            frames[k] = ((1 - mix) * l + both) * gl;
            frames[k + 1] = ((1 - mix) * r + both) * gr;
        }
    }
}
