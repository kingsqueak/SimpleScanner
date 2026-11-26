namespace SimpleScanner.Tests;

/// <summary>
/// The mixer on its own, with made-up audio: each slot's sound through its fade to its seat, the sum,
/// the limiter and the radio's pan.
/// </summary>
public class SlotMixerTests
{
    private const int Rate = 48000;
    private const int GlideSamples = (int)(SlotMixer.GlideSeconds * Rate);

    /// <summary>One slot and the audio made up for it: sample k of its left and of its right.</summary>
    private sealed class Voice(Func<long, float> left, Func<long, float> right)
    {
        public readonly SlotSound Sound = new();
        public Func<long, float> Left = left, Right = right;
        public bool Open = true;
        public long Given;

        public Voice(Func<long, float> both) : this(both, both) { }

        public void Give(int count)
        {
            var l = new float[count];
            var r = new float[count];
            for (int k = 0; k < count; k++)
            {
                l[k] = Left(Given + k);
                r[k] = Right(Given + k);
            }
            Sound.Give(l, r, Open);
            Given += count;
        }
    }

    /// <summary>What has come out of a mixer, left and right apart.</summary>
    private sealed class Output
    {
        public readonly List<float> Left = [], Right = [];
        public int Count => Left.Count;

        public int Add(ReadOnlySpan<float> frames)
        {
            for (int k = 0; k + 1 < frames.Length; k += 2)
            {
                Left.Add(frames[k]);
                Right.Add(frames[k + 1]);
            }
            return frames.Length / 2;
        }
    }

    private static Func<long, float> Tone(double hz, double amplitude) => k => (float)(amplitude * Math.Sin(2 * Math.PI * hz * k / Rate));

    private static SlotSound[] Sounds(params Voice[] voices) => voices.Select(v => v.Sound).ToArray();

    /// <summary>Every voice gives blocks of this many samples and the mixer mixes them, for the given time.</summary>
    private static void Play(SlotMixer mixer, Voice[] voices, Output output, double seconds, int block = 480, double volume = 1, double pan = 0)
    {
        var sounds = Sounds(voices);
        for (int left = (int)Math.Round(seconds * Rate); left > 0; left -= block)
        {
            foreach (var v in voices) v.Give(Math.Min(block, left));
            output.Add(mixer.Mix(sounds, volume, pan));
        }
    }

    /// <summary>The amplitude of a tone in the last tenth of a second of a signal (the tone a multiple of 10 Hz).</summary>
    private static double Level(List<float> x, double hz)
    {
        int n = Rate / 10, from = x.Count - n;
        double s = 0, c = 0;
        for (int k = 0; k < n; k++)
        {
            double w = 2 * Math.PI * hz * k / Rate;
            s += x[from + k] * Math.Sin(w);
            c += x[from + k] * Math.Cos(w);
        }
        return 2 * Math.Sqrt(s * s + c * c) / n;
    }

    // ---- one slot is what the receiver did before ----

    // Receiver.FadeAndPan as it was before the mixer (0.1.4): a slot's audio into stereo frames, at the volume and pan.
    private static void FadeAndPanAsItWas(float[] left, float[] right, bool open, double volume, double panSetting, ref float gain, Span<float> a, int count)
    {
        float target = open ? (float)(volume * volume) : 0f, g = gain;
        const float step = 1f / 240;
        float pan = (float)panSetting;
        if (pan == 0)
        {
            for (int k = 0; k < count; k++)
            {
                g += Math.Clamp(target - g, -step, step);
                a[2 * k] = left[k] * g;
                a[2 * k + 1] = right[k] * g;
            }
        }
        else
        {
            var (gl, gr, mix) = Receiver.PanGains(pan);
            for (int k = 0; k < count; k++)
            {
                g += Math.Clamp(target - g, -step, step);
                float l = left[k], r = right[k], both = mix * 0.5f * (l + r);
                a[2 * k] = ((1 - mix) * l + both) * g * gl;
                a[2 * k + 1] = ((1 - mix) * r + both) * g * gr;
            }
        }
        gain = g;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(-1, 0)]
    [InlineData(-0.4, 0)]
    [InlineData(0.4, 3)]
    [InlineData(1, 0)]
    public void OneSlotSoundsAsItDidBeforeTheMixer(double pan, int seat)
    {
        var rng = new Random(4);
        int[] sizes = [409, 410, 411, 0, 1, 7, 512, 96];
        var mixer = new SlotMixer();
        var sound = new SlotSound();
        sound.Take(seat);
        SlotSound[] sounds = [sound];
        float gain = 0;
        long at = 0, compared = 0, different = 0;
        double worst = 0;
        bool sawFadeIn = false, sawFadeOut = false;
        for (int b = 0; b < 700; b++)
        {
            // Shut, open (a fade in), the volume changed part way, shut (a fade out), open again, then
            // opening and shutting faster than the fade.
            bool open = b is >= 20 and < 200 or >= 260 and < 480 || (b >= 520 && b % 3 == 0);
            double volume = b < 120 ? 0.5 : b < 300 ? 0.9 : b < 380 ? 0.2 : b < 600 ? 1 : 0.7;
            int count = sizes[rng.Next(sizes.Length)];
            var left = new float[count];
            var right = new float[count];
            for (int k = 0; k < count; k++, at++)
            {
                // Stereo: the left is not the right. Full scale, both zeros and the odd sample of noise among them.
                left[k] = (float)Math.Clamp(1.1 * Math.Sin(2 * Math.PI * 440 * at / Rate) + 0.1 * (rng.NextDouble() - 0.5), -1, 1);
                right[k] = (float)Math.Clamp(0.7 * Math.Sin(2 * Math.PI * 587 * at / Rate + 1) + 0.3 * (rng.NextDouble() - 0.5), -1, 1);
                if (rng.Next(50) == 0) left[k] = -0f;
                if (rng.Next(50) == 0) right[k] = 0f;
            }

            var expected = new float[2 * count];
            float before = gain;
            FadeAndPanAsItWas(left, right, open, volume, pan, ref gain, expected, count);
            sawFadeIn |= gain > before && before > 0;
            sawFadeOut |= gain < before && gain > 0;

            sound.Give(left, right, open);
            var got = mixer.Mix(sounds, volume, pan);
            Assert.Equal(expected.Length, got.Length);      // the whole block, nothing kept back
            Assert.Equal(open, mixer.AnyOpen);
            for (int k = 0; k < got.Length; k++, compared++)
            {
                if (BitConverter.SingleToInt32Bits(got[k]) != BitConverter.SingleToInt32Bits(expected[k])) different++;
                worst = Math.Max(worst, Math.Abs((double)got[k] - expected[k]));
            }
            Assert.Equal(gain, sound.Gain);
        }
        Assert.True(sawFadeIn && sawFadeOut && compared > 200_000);
        Assert.Equal(0.0, sound.Position);      // alone: in the centre, whatever its seat
        if (pan == 0) Assert.Equal(0, different);       // bit for bit
        else Assert.True(worst <= 1e-6, $"{worst} away from what it was");
    }

    // ---- the seats ----

    // Each slot has a tone of its own in its left and another in its right, so that each can be found in the output.
    private static Voice[] FourVoices() => Enumerable.Range(0, 4)
        .Select(i => new Voice(Tone(300 + 200 * i, 0.1), Tone(400 + 200 * i, 0.1))).ToArray();

    /// <summary>How much of a slot's left tone and of its right tone must be in each speaker when it sits at a position.</summary>
    private static void AssertAt(double position, int slot, Output output)
    {
        var (gl, gr, mix) = Receiver.PanGains(position);
        double own = 1 - mix + mix / 2.0, other = mix / 2.0;     // the far side is brought in by the same amount
        double leftTone = 300 + 200 * slot, rightTone = 400 + 200 * slot;
        Assert.Equal(0.1 * own * gl, Level(output.Left, leftTone), 5);
        Assert.Equal(0.1 * other * gr, Level(output.Right, leftTone), 5);
        Assert.Equal(0.1 * other * gl, Level(output.Left, rightTone), 5);
        Assert.Equal(0.1 * own * gr, Level(output.Right, rightTone), 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ASlotAloneIsInTheCentreWhateverItsSeat(int seat)
    {
        var voices = FourVoices();
        voices[seat].Sound.Take(seat);
        var mixer = new SlotMixer();
        var output = new Output();
        // The other three are being worked on too, and hold nothing.
        foreach (var v in voices) v.Open = v == voices[seat];
        Play(mixer, voices, output, 0.4);
        AssertAt(0, seat, output);
        Assert.Equal(0.0, voices[seat].Sound.Position);
        // Its left is in the left speaker only and its right in the right, as they came.
        Assert.Equal(0, Level(output.Right, 300 + 200 * seat), 7);
        Assert.Equal(0, Level(output.Left, 400 + 200 * seat), 7);
    }

    [Fact]
    public void TwoThreeAndFourSlotsSitInTheirSeats()
    {
        var voices = FourVoices();
        var mixer = new SlotMixer();
        var output = new Output();
        foreach (var v in voices) v.Open = false;

        void Join(int slot)
        {
            voices[slot].Sound.Take(slot);
            voices[slot].Open = true;
            Play(mixer, voices, output, 0.4);
        }

        Join(0);
        AssertAt(0, 0, output);
        Join(1);
        AssertAt(-0.33, 0, output);
        AssertAt(0.33, 1, output);
        Assert.Equal(-0.33, voices[0].Sound.Position);
        Assert.Equal(0.33, voices[1].Sound.Position);

        // The third comes out of the left speaker only, and the first two stay where they are.
        Join(2);
        AssertAt(-0.33, 0, output);
        AssertAt(0.33, 1, output);
        AssertAt(-1, 2, output);
        Assert.Equal(0, Level(output.Right, 700), 7);
        Assert.Equal(0, Level(output.Right, 800), 7);
        Assert.Equal(0.1, Level(output.Left, 700) + Level(output.Left, 800), 5);     // both of its channels are there

        // The fourth out of the right only.
        Join(3);
        AssertAt(-0.33, 0, output);
        AssertAt(0.33, 1, output);
        AssertAt(-1, 2, output);
        AssertAt(1, 3, output);
        Assert.Equal(0, Level(output.Left, 900), 7);
        Assert.Equal(0, Level(output.Left, 1000), 7);
        Assert.Equal(0.1, Level(output.Right, 900) + Level(output.Right, 1000), 5);
        Assert.Equal(new[] { -0.33, 0.33, -1.0, 1.0 }, voices.Select(v => v.Sound.Position));
    }

    // ---- the glide ----

    /// <summary>
    /// The gain a tone of amplitude 0.5 came out with, sample by sample, where the tone is large enough to
    /// tell (elsewhere: -1), and the largest change of that gain between two neighbouring samples.
    /// </summary>
    private static (double[] Gain, double LargestStep) GainOf(List<float> output, int from, Func<long, float> tone)
    {
        var gain = new double[output.Count - from];
        double largest = 0;
        for (int k = 0; k < gain.Length; k++)
        {
            float x = tone(from + k);
            gain[k] = Math.Abs(x) >= 0.1 ? output[from + k] / x : -1;
            if (k > 0 && gain[k] >= 0 && gain[k - 1] >= 0) largest = Math.Max(largest, Math.Abs(gain[k] - gain[k - 1]));
        }
        return (gain, largest);
    }

    [Fact]
    public void ASlotGlidesToItsSeatWhenASecondTakesAChannelAndBackWhenItIsAloneAgain()
    {
        var tone = Tone(440, 0.5);
        Voice first = new(tone), second = new(_ => 0f) { Open = false };
        Voice[] voices = [first, second];
        var mixer = new SlotMixer();
        var output = new Output();
        first.Sound.Take(0);
        // Blocks of an awkward size: a gain that moved once a block would show as steps.
        Play(mixer, voices, output, 0.1, block: 409);
        for (int k = 4000; k < output.Count; k++) Assert.Equal(tone(k), output.Right[k]);     // in the centre, as it came

        // A second slot takes a channel: it is in its seat at once, and the first one moves to its own.
        int taken = output.Count;
        second.Sound.Take(1);
        Play(mixer, voices, output, 0.3, block: 409);
        Assert.Equal(0.33, second.Sound.Position);
        Assert.Equal(-0.33, first.Sound.Position);
        var (gain, step) = GainOf(output.Right, taken, tone);
        double perSample = 0.33 / GlideSamples;
        Assert.True(step <= perSample + 5e-6, $"the gain moved by {step} from one sample to the next, not {perSample}");
        // A third of the way there after a third of the time, and there after about 150 ms.
        int arrived = Array.FindIndex(gain, g => g >= 0 && g <= 0.67 + 1e-5);
        Assert.InRange(arrived, GlideSamples - 20, GlideSamples + 20);
        Assert.All(gain.Skip(arrived), g => Assert.True(g < 0 || Math.Abs(g - 0.67) < 1e-5));
        int third = Array.FindIndex(gain, GlideSamples / 3, g => g >= 0);
        Assert.Equal(1 - 0.33 * third / GlideSamples, gain[third], 3);
        // The left, the side it moves towards, is not turned down.
        Assert.All(GainOf(output.Left, taken, tone).Gain, g => Assert.True(g < 0 || Math.Abs(g - 1) < 1e-5));

        // The second lets go: the first is alone and glides back, as smoothly.
        int left = output.Count;
        second.Sound.Leave();
        Play(mixer, voices, output, 0.3, block: 409);
        Assert.Equal(0.0, first.Sound.Position);
        (gain, step) = GainOf(output.Right, left, tone);
        Assert.True(step <= perSample + 5e-6, $"the gain moved by {step} from one sample to the next, not {perSample}");
        arrived = Array.FindIndex(gain, g => g >= 1 - 1e-5);
        Assert.InRange(arrived, GlideSamples - 20, GlideSamples + 20);
        for (int k = left + GlideSamples + 20; k < output.Count; k++) Assert.Equal(tone(k), output.Right[k]);
    }

    [Fact]
    public void ASlotWhoseSquelchClosesFadesOutAndKeepsItsSeatAndTheOtherDoesNotMove()
    {
        Voice first = new(Tone(300, 0.2)), second = new(Tone(500, 0.2));
        Voice[] voices = [first, second];
        var mixer = new SlotMixer();
        var output = new Output();
        first.Sound.Take(0);
        second.Sound.Take(1);
        Play(mixer, voices, output, 0.4);
        Assert.Equal(0.2, Level(output.Left, 300), 5);
        Assert.Equal(0.2 * 0.67, Level(output.Right, 300), 5);
        Assert.Equal(0.2 * 0.67, Level(output.Left, 500), 5);
        Assert.Equal(0.2, Level(output.Right, 500), 5);
        Assert.True(mixer.AnyOpen);

        // The second one's squelch closes, as in a pause: a fade of a few milliseconds, not a cut.
        second.Open = false;
        Play(mixer, voices, output, 0.002);
        Assert.InRange(second.Sound.Gain, 0.3f, 0.9f);
        Play(mixer, voices, output, 0.008);
        Assert.Equal(0f, second.Sound.Gain);
        Play(mixer, voices, output, 0.39);
        Assert.Equal(0, Level(output.Left, 500), 7);
        Assert.Equal(0, Level(output.Right, 500), 7);
        // It still holds its channel: both are where they were.
        Assert.True(second.Sound.Holding);
        Assert.Equal(0.33, second.Sound.Position);
        Assert.Equal(-0.33, first.Sound.Position);
        Assert.Equal(0.2, Level(output.Left, 300), 5);
        Assert.Equal(0.2 * 0.67, Level(output.Right, 300), 5);
        Assert.True(mixer.AnyOpen);

        // The reply comes from the same place.
        second.Open = true;
        Play(mixer, voices, output, 0.4);
        Assert.Equal(0.2 * 0.67, Level(output.Left, 500), 5);
        Assert.Equal(0.2, Level(output.Right, 500), 5);

        first.Open = second.Open = false;
        Play(mixer, voices, output, 0.1);
        Assert.False(mixer.AnyOpen);
        Assert.All(output.Left.Skip(output.Count - 4000), x => Assert.Equal(0f, x));
    }

    // ---- the limiter ----

    /// <summary>Four slots in their seats, each with one tone in both channels: the sum as the mixer makes it, with no limiter.</summary>
    private static (float Left, float Right) SumInSeats(float[] samples)
    {
        float left = 0, right = 0;
        for (int slot = 0; slot < samples.Length; slot++)
        {
            var (gl, gr, mix) = Receiver.PanGains(SlotMixer.SeatPosition(slot));
            float x = samples[slot], both = mix * 0.5f * (x + x);
            float l = ((1 - mix) * x + both) * gl, r = ((1 - mix) * x + both) * gr;
            if (slot == 0) (left, right) = (l, r);
            else (left, right) = (left + l, right + r);
        }
        return (left, right);
    }

    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    [Fact]
    public void ASumWithinFullScaleIsNotTouched()
    {
        // Four voices in step whose sum comes to within a hair of full scale in the left speaker (2.67 of one).
        double amplitude = 0.9999 / 2.67;
        var voices = Enumerable.Range(0, 4).Select(_ => new Voice(Tone(1000, amplitude))).ToArray();
        for (int i = 0; i < 4; i++) voices[i].Sound.Take(i);
        var mixer = new SlotMixer();
        var output = new Output();
        Play(mixer, voices, output, 1, block: 411);
        Assert.InRange(output.Left.Max(), 0.999f, 1f);
        var tone = Tone(1000, amplitude);
        for (int k = 1000; k < output.Count; k++)
        {
            float x = tone(k);
            var (l, r) = SumInSeats([x, x, x, x]);
            Assert.True(Same(l, output.Left[k]) && Same(r, output.Right[k]), $"sample {k}: {output.Left[k]}, {output.Right[k]} for {l}, {r}");
        }
    }

    [Fact]
    public void FourFullScaleVoicesInStepStayWithinFullScaleAndTheGainComesBack()
    {
        double amplitude = 1;
        var voices = Enumerable.Range(0, 4).Select(_ => new Voice(k => (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * k / Rate)))).ToArray();
        for (int i = 0; i < 4; i++) voices[i].Sound.Take(i);
        var mixer = new SlotMixer();
        var output = new Output();
        Play(mixer, voices, output, 0.5, block: 411);
        Assert.All(output.Left, x => Assert.InRange(x, -1f, 1f));
        Assert.All(output.Right, x => Assert.InRange(x, -1f, 1f));
        // Turned down to full scale and no further, and still the tone: 2.67 of one voice in each speaker.
        // (A tone whose very top is taken off now and then measures a hair over 1.)
        Assert.InRange(output.Left.Skip(Rate / 4).Max(), 0.999f, 1f);
        Assert.InRange(Level(output.Left, 1000), 0.97, 1.01);
        Assert.Equal(Level(output.Left, 1000), Level(output.Right, 1000), 4);

        // The peak has passed: what follows is within full scale. The gain is back, and the sum is let
        // through bit for bit, within a quarter of a second, and not at once.
        int quiet = output.Count;
        amplitude = 0.2;
        Play(mixer, voices, output, 0.6, block: 411);
        int last = quiet - 1;
        for (int k = quiet; k < output.Count; k++)
        {
            float x = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * k / Rate));
            var (l, r) = SumInSeats([x, x, x, x]);
            if (!Same(l, output.Left[k]) || !Same(r, output.Right[k])) last = k;
            Assert.True(Math.Abs(output.Left[k]) <= Math.Abs(l) && Math.Abs(output.Right[k]) <= Math.Abs(r));     // on the way back it only turns down
        }
        double seconds = (last + 1 - quiet) / (double)Rate;
        Assert.InRange(seconds, 0.05, 0.25);
    }

    [Fact]
    public void TheLimiterTurnsLeftAndRightDownTogether()
    {
        // Two full-scale voices in step, one 33% left and one hard left: twice full scale in the left
        // speaker and 0.67 of it, well within, in the right. Both come down by half, so the sound stays where it was.
        Voice[] voices = [new(Tone(1000, 1)), new(Tone(1000, 1))];
        voices[0].Sound.Take(0);
        voices[1].Sound.Take(2);
        var mixer = new SlotMixer();
        var output = new Output();
        Play(mixer, voices, output, 0.5, block: 411);
        Assert.InRange(output.Left.Skip(Rate / 4).Max(), 0.999f, 1f);
        Assert.InRange(output.Right.Skip(Rate / 4).Max(), 0.33f, 0.34f);
        Assert.Equal(0.67 / 2, Level(output.Right, 1000) / Level(output.Left, 1000), 3);
    }

    // ---- slots that are not in step ----

    /// <summary>
    /// Samples a channel gives for block b: 100 or 101, as a filter that takes one sample in every so many
    /// does, each channel at its own point in that cycle.
    /// </summary>
    private static int CountFor(int block, double phase) => (int)Math.Floor((block + 1) * 100.37 + phase) - (int)Math.Floor(block * 100.37 + phase);

    private static Func<long, float> Noise(int seed)
    {
        // The same sample for the same k whenever it is asked for.
        return k => (float)(((k * 2654435761L + seed * 40503L) % 9973) / 9973.0 - 0.5);
    }

    [Fact]
    public void SlotsWhoseBlocksDifferByASampleLoseAndRepeatNothing()
    {
        // Hard left and hard right: what comes out of each speaker is one slot's samples. A third, silent, is out of step with both.
        Func<long, float> a = Noise(1), b = Noise(2);
        Voice left = new(a), right = new(b), silent = new(_ => 0f);
        left.Sound.Take(2);
        right.Sound.Take(3);
        silent.Sound.Take(0);
        Voice[] voices = [silent, left, right];
        double[] phases = [0.5, 0, 0.7];
        var sounds = Sounds(voices);
        var mixer = new SlotMixer();
        var output = new Output();
        int kept = 0, differed = 0;
        for (int block = 0; block < 6000; block++)
        {
            int[] counts = [.. phases.Select(p => CountFor(block, p))];
            if (counts.Distinct().Count() > 1) differed++;
            for (int i = 0; i < 3; i++) voices[i].Give(counts[i]);
            output.Add(mixer.Mix(sounds, 1, 0));
            kept = Math.Max(kept, sounds.Max(s => s.Pending));
            // Everything every slot has given is out, but the sample or two one is ahead by.
            Assert.Equal((int)voices.Min(v => v.Given), output.Count);
        }
        Assert.True(differed > 1000);
        Assert.InRange(kept, 1, 2);
        // After the fade in, the left speaker is the one slot's samples in order and the right the other's.
        for (int k = 0; k < output.Count; k++)
        {
            if (k < 300)
            {
                Assert.True(Math.Abs(output.Left[k]) <= Math.Abs(a(k)) && Math.Abs(output.Right[k]) <= Math.Abs(b(k)));
                continue;
            }
            Assert.Equal(a(k), output.Left[k]);
            Assert.Equal(b(k), output.Right[k]);
        }
    }

    [Fact]
    public void ASlotThatJoinsLateDoesNotDelayTheOthers()
    {
        Func<long, float> a = Noise(3), b = Noise(4);
        Voice playing = new(a), late = new(b) { Open = false };
        playing.Sound.Take(2);          // hard left when it has company: the left speaker is its samples
        SlotSound[] alone = [playing.Sound], both = [playing.Sound, late.Sound];
        var mixer = new SlotMixer();
        var output = new Output();
        for (int block = 0; block < 400; block++)
        {
            int count = CountFor(block, 0);
            playing.Give(count);
            if (block < 100)
            {
                // The other slot is free.
                Assert.Equal(count, output.Add(mixer.Mix(alone, 1, 0)));
            }
            else if (block < 110)
            {
                // It has been given a channel and has nothing to give yet: the first one's block comes out whole.
                late.Give(0);
                Assert.Equal(count, output.Add(mixer.Mix(both, 1, 0)));
            }
            else
            {
                // Now it gives audio, at its own point in the cycle, and takes its seat.
                if (block == 110)
                {
                    late.Sound.Take(3);
                    late.Open = true;
                }
                late.Give(CountFor(block, 0.6));
                int n = output.Add(mixer.Mix(both, 1, 0));
                Assert.InRange(n, count - 1, count + 1);
                Assert.InRange(playing.Sound.Pending, 0, 2);
            }
        }
        // The first slot's samples all came out, in order: none lost or repeated, and no more than a sample or two late.
        Assert.InRange(output.Count, (int)playing.Given - 2, (int)playing.Given);
        for (int k = 300; k < output.Count; k++)
        {
            // In the centre until the other took its seat, then gliding to the left speaker: there its gain is 1 throughout.
            Assert.True(Math.Abs(a(k) - output.Left[k]) < 1e-6);
        }
        // And the late one is in the right speaker from where it began, in order (looked at once the first has left that speaker).
        int begun = output.Count - ((int)late.Given - late.Sound.Pending);
        Assert.InRange(begun, 110 * 100, 110 * 101 + 2);
        for (int k = begun + GlideSamples + 10; k < output.Count; k++) Assert.Equal(b(k - begun), output.Right[k]);
    }

    [Fact]
    public void ASlotThatStopsGivingDoesNotStopTheOthers()
    {
        Func<long, float> a = Noise(5);
        Voice playing = new(a), stuck = new(Tone(700, 0.2));
        playing.Sound.Take(2);
        stuck.Sound.Take(3);
        Voice[] voices = [playing, stuck];
        var sounds = Sounds(voices);
        var mixer = new SlotMixer();
        var output = new Output();
        Play(mixer, voices, output, 0.2, block: 1);

        // One sample to a block, and the second slot's channel gives nothing more. The first waits for it by
        // no more than a channel can be behind, then plays on with nothing lost.
        for (int block = 0; block < 2000; block++)
        {
            playing.Give(1);
            stuck.Give(0);
            output.Add(mixer.Mix(sounds, 1, 0));
            Assert.InRange(playing.Sound.Pending, 0, SlotMixer.CarryMost + 1);
        }
        Assert.InRange(output.Count, (int)playing.Given - SlotMixer.CarryMost - 1, (int)playing.Given);
        for (int k = 300; k < output.Count; k++) Assert.Equal(a(k), output.Left[k]);

        // When it gives again it is heard again.
        Play(mixer, voices, output, 0.3, block: 100);
        Assert.Equal(0.2, Level(output.Right, 700), 4);
        for (int k = 300; k < output.Count; k++) Assert.Equal(a(k), output.Left[k]);
    }
}
