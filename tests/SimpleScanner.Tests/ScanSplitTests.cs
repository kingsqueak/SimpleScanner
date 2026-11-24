using SimpleScanner.Dsp;

namespace SimpleScanner.Tests;

/// <summary>Sharing a scan list among the radios that are on.</summary>
public class ScanSplitTests
{
    private const double Usable = 6e6;

    private static ScanChannel Ch(double mhz) => new("", mhz * 1e6, Mode.Nfm, BandPlan.NfmNarrow);

    // Channels 25 kHz apart from each of these starts: each block fits one tuning, and the blocks are too far apart to share one.
    private static List<ScanChannel> Blocks(int each, params double[] startsMHz) =>
        startsMHz.SelectMany(s => Enumerable.Range(0, each).Select(k => Ch(s + 0.025 * k))).ToList();

    private static int[] Any(int count) => Enumerable.Repeat(-1, count).ToArray();

    private static void AssertWhole(IReadOnlyList<ScanChannel> all, List<ScanChannel>[] shares)
    {
        // Every channel is with exactly one radio, and each radio's are in frequency order.
        Assert.Equal(all.Select(c => c.FrequencyHz).Order(), shares.SelectMany(s => s).Select(c => c.FrequencyHz).Order());
        foreach (var share in shares)
            Assert.Equal(share.Select(c => c.FrequencyHz).Order(), share.Select(c => c.FrequencyHz));
    }

    [Fact]
    public void OneRadioTakesEverythingWhateverTheGroupsSay()
    {
        var all = Blocks(4, 150, 160, 460);
        var shares = ScanSplit.Split(all, [.. Any(8), 0, 0, 0, 0], [Usable]);
        Assert.Equal(all, Assert.Single(shares));
    }

    [Fact]
    public void TwoRadiosEachTakeOneEndOfTheSpectrumWithTheTuningsShared()
    {
        var all = Blocks(5, 120, 150, 160, 460, 470, 850);
        Assert.Equal(6, ScanSplit.Windows(all, Usable));
        var shares = ScanSplit.Split(all, Any(all.Count), [Usable, Usable]);
        AssertWhole(all, shares);
        Assert.Equal(3, ScanSplit.Windows(shares[0], Usable));
        Assert.Equal(3, ScanSplit.Windows(shares[1], Usable));
        // Runs, not a shuffle: all of the first radio's are below all of the second's.
        Assert.True(shares[0][^1].FrequencyHz < shares[1][0].FrequencyHz);
    }

    [Fact]
    public void AnOddNumberOfTuningsLeavesNoRadioMoreThanOneAhead()
    {
        var all = Blocks(3, 120, 150, 160, 460, 470, 850, 900);
        var shares = ScanSplit.Split(all, Any(all.Count), [Usable, Usable, Usable]);
        AssertWhole(all, shares);
        var counts = shares.Select(s => ScanSplit.Windows(s, Usable)).ToArray();
        Assert.Equal(7, counts.Sum());
        Assert.Equal(3, counts.Max());
        Assert.True(counts.Min() >= 2, string.Join(",", counts));
    }

    [Fact]
    public void AGroupsChannelsGoToItsRadioAndTheRestMakeUpTheBalance()
    {
        // The 460 and 470 blocks are given to the second radio; the other four are anyone's.
        var all = Blocks(5, 120, 150, 160, 460, 470, 850);
        int[] wanted = all.Select(c => c.FrequencyHz is > 455e6 and < 475e6 ? 1 : -1).ToArray();
        var shares = ScanSplit.Split(all, wanted, [Usable, Usable]);
        AssertWhole(all, shares);
        Assert.All(all.Where((_, i) => wanted[i] == 1), c => Assert.Contains(c, shares[1]));
        Assert.Equal(3, ScanSplit.Windows(shares[0], Usable));
        Assert.Equal(3, ScanSplit.Windows(shares[1], Usable));
        // The second radio's extra run is the top of what was left, since the first has the bottom.
        Assert.Contains(all[^1], shares[1]);
        Assert.Contains(all[0], shares[0]);
    }

    [Fact]
    public void ARadioWithMoreOfItsOwnThanAFairShareGetsNothingElse()
    {
        var all = Blocks(5, 120, 150, 160, 460, 470, 850);
        int[] wanted = all.Select(c => c.FrequencyHz > 140e6 ? 0 : -1).ToArray();
        var shares = ScanSplit.Split(all, wanted, [Usable, Usable]);
        AssertWhole(all, shares);
        Assert.Equal(25, shares[0].Count);
        Assert.Equal(5, shares[1].Count);
    }

    [Fact]
    public void ARadioBeyondOneThatIsFullOfItsOwnStillTakesAShare()
    {
        // The middle radio has six tunings of its own; the four blocks left are for the first and the third, two each.
        var own = Blocks(3, 200, 210, 220, 230, 240, 250);
        var free = Blocks(3, 120, 150, 460, 850);
        var all = own.Concat(free).OrderBy(c => c.FrequencyHz).ToList();
        int[] wanted = all.Select(c => own.Contains(c) ? 1 : -1).ToArray();
        var shares = ScanSplit.Split(all, wanted, [Usable, Usable, Usable]);
        AssertWhole(all, shares);
        Assert.Equal(own, shares[1]);
        Assert.Equal(2, ScanSplit.Windows(shares[0], Usable));
        Assert.Equal(2, ScanSplit.Windows(shares[2], Usable));
        Assert.True(shares[0][^1].FrequencyHz < shares[2][0].FrequencyHz);
    }

    [Fact]
    public void EverythingGivenToOneRadioLeavesTheOtherWithNone()
    {
        var all = Blocks(5, 150, 460);
        var shares = ScanSplit.Split(all, Enumerable.Repeat(1, all.Count).ToArray(), [Usable, Usable]);
        Assert.Empty(shares[0]);
        Assert.Equal(all, shares[1]);
    }

    [Fact]
    public void MoreRadiosThanTuningsLeavesSomeIdleAndLosesNothing()
    {
        var all = Blocks(4, 150);
        var shares = ScanSplit.Split(all, Any(all.Count), [Usable, Usable, Usable]);
        AssertWhole(all, shares);
        Assert.Equal(4, shares.Sum(s => s.Count));
        Assert.Empty(ScanSplit.Split([], [], [Usable, Usable])[1]);
        Assert.Empty(ScanSplit.Split(all, Any(all.Count), []));
    }

    [Fact]
    public void ANarrowerRadioIsGivenFewerChannelsForTheSameNumberOfTunings()
    {
        // Eight blocks 2 MHz apart: the wide radio watches three of them from one tuning, the narrow one a block.
        var all = Blocks(8, 150, 152, 154, 156, 158, 160, 162, 164);
        Assert.Equal(3, ScanSplit.Windows(all, Usable));
        Assert.Equal(8, ScanSplit.Windows(all, 1.5e6));
        var shares = ScanSplit.Split(all, Any(all.Count), [Usable, 1.5e6]);
        AssertWhole(all, shares);
        Assert.Equal(2, ScanSplit.Windows(shares[0], Usable));
        Assert.Equal(2, ScanSplit.Windows(shares[1], 1.5e6));
        Assert.Equal(48, shares[0].Count);
        Assert.Equal(16, shares[1].Count);
    }

    [Fact]
    public void AGroupsRadioIsTheFirstOfItsGroupsThatHasOneThatIsOn()
    {
        var given = new Dictionary<string, string> { ["Air"] = "A", ["Rail"] = "B", ["Marine"] = "GONE" };
        string?[] on = ["A", "B"];
        Assert.Equal(0, ScanSplit.RadioFor(["Air"], given, on));
        Assert.Equal(1, ScanSplit.RadioFor(["Rail", "Air"], given, on));
        Assert.Equal(-1, ScanSplit.RadioFor(["Police"], given, on));
        Assert.Equal(-1, ScanSplit.RadioFor([], given, on));
        // A radio that is not on: the group counts as anyone's, and a later group may still name one.
        Assert.Equal(-1, ScanSplit.RadioFor(["Marine"], given, on));
        Assert.Equal(0, ScanSplit.RadioFor(["Marine", "Air"], given, on));
        Assert.Equal(-1, ScanSplit.RadioFor(["Rail"], given, ["A"]));
        // A tag's group keeps its radio when the tag is written another way.
        var tags = new Dictionary<string, string> { [MemoryStore.TagPrefix + "Fire Dispatch"] = "B" };
        Assert.Equal(1, ScanSplit.RadioFor([MemoryStore.TagPrefix + "fire dispatch"], tags, on));
    }

    [Fact]
    public void AListOfSomeThousandsIsSharedOutQuickly()
    {
        var rng = new Random(7);
        var all = Enumerable.Range(0, 3000).Select(_ => Ch(Math.Round(rng.Next(118_000, 470_000) / 12.5) * 12.5 / 1000))
            .DistinctBy(c => c.FrequencyHz).OrderBy(c => c.FrequencyHz).ToList();
        int[] wanted = all.Select((_, i) => i % 7 == 0 ? 1 : -1).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var shares = ScanSplit.Split(all, wanted, [Usable, Usable]);
        sw.Stop();
        AssertWhole(all, shares);
        Assert.InRange(ScanSplit.Windows(shares[0], Usable) - ScanSplit.Windows(shares[1], Usable), -1, 1);
        Assert.True(sw.Elapsed.TotalSeconds < 2, $"took {sw.Elapsed.TotalSeconds:F2} s");
    }
}
