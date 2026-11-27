// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleScanner.Dsp;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Tests;

/// <summary>
/// Several radios scan the one set of stored channels, each by its own groups and independently of the
/// others: channels the radios' groups have in common are scanned by every radio that has them.
/// </summary>
public class IndependentScanTests
{
    // Too far apart to share a tuning: each is a tuning of its own, so a radio's tunings count its channels.
    private static MemoryChannel Ch(string name, double mhz, string group) => new() { Name = name, FrequencyHz = mhz * 1e6, Groups = [group] };

    [Fact]
    public async Task EachRadioScansAllOfItsOwnGroupsWhateverTheOtherScans()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ss-ind-settings-{Guid.NewGuid():N}.json");
        string db = Path.Combine(Path.GetTempPath(), $"ss-ind-{Guid.NewGuid():N}.db");
        try
        {
            var store = new MemoryStore(db);
            store.Channels.AddRange([Ch("Air 1", 120, "Air"), Ch("Air 2", 130, "Air"), Ch("Air 3", 140, "Air"),
                Ch("Fire 1", 150, "Fire"), Ch("Fire 2", 160, "Fire")]);
            store.Save();
            var settings = Settings.Load(path);
            settings.Entry("A").Groups = ["Air", "Fire"];
            settings.Entry("B").Groups = ["Air"];
            static Receiver Rx(string serial) => new(new FakeSource { Serial = serial }, playAudio: false, sampleRate: 2.4e6);
            var rxA = Rx("A");
            var rxB = Rx("B");
            using (rxA)
            using (rxB)
            {
                var main = new MainViewModel(settings, [rxA, rxB], MemoryStore.Load(db));
                var (a, b) = (main.RadioList[0], main.RadioList[1]);
                await main.StartAll();
                Assert.True(a.IsOn && b.IsOn);

                main.Select(a);
                Assert.Equal("Active Frequencies (5)", main.ActiveTitle);
                main.ToggleScanCommand.Execute(null);
                Assert.Equal(5, rxA.ScanWindows);
                Assert.Equal("Active Frequencies (5)", main.ActiveTitle);

                // The second radio's scan takes nothing from the first's: the Air channels are scanned by both.
                main.Select(b);
                Assert.Equal("Active Frequencies (3)", main.ActiveTitle);
                main.ToggleScanCommand.Execute(null);
                Assert.True(rxA.Scanning && rxB.Scanning);
                Assert.Equal(3, rxB.ScanWindows);
                Assert.Equal(5, rxA.ScanWindows);
                Assert.Equal("Active Frequencies (3)", main.ActiveTitle);
                main.Select(a);
                Assert.Equal("Active Frequencies (5)", main.ActiveTitle);

                // One stopping leaves the other's scan as it was.
                main.Select(b);
                main.ToggleScanCommand.Execute(null);
                Assert.False(rxB.Scanning);
                Assert.Equal(5, rxA.ScanWindows);
                Assert.Equal("Active Frequencies (3)", main.ActiveTitle);

                // A channel unticked is left out by every radio that scans it; the stored tick is common to all.
                main.ToggleScanCommand.Execute(null);
                main.ActiveFrequencies[0].Scan = false;       // Air 1, on B's list
                Assert.Equal(4, rxA.ScanWindows);
                Assert.Equal(2, rxB.ScanWindows);

                // Unticking all of one radio's channels stops that radio only.
                main.ActiveFrequencies[1].Scan = false;
                main.ActiveFrequencies[2].Scan = false;
                Assert.False(rxB.Scanning);
                Assert.Contains("No channels are ticked", b.Status);
                Assert.True(rxA.Scanning);
                Assert.Equal(2, rxA.ScanWindows);
                main.Shutdown();
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(db);
        }
    }

    [Fact]
    public void NoGroupTickedIsNothingAndBothButtonsWork()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ss-ind-settings-{Guid.NewGuid():N}.json");
        string db = Path.Combine(Path.GetTempPath(), $"ss-ind-{Guid.NewGuid():N}.db");
        try
        {
            var store = new MemoryStore(db);
            store.Channels.AddRange([Ch("Air 1", 120, "Air"), Ch("Fire 1", 150, "Fire")]);
            store.Save();
            // As a build before 0.1.6 wrote it: an empty list was every group.
            var settings = Settings.Load(path);
            settings.Entry("A").Groups = [];
            settings.Save();
            using var rx = new Receiver(new FakeSource { Serial = "A" }, playAudio: false, sampleRate: 2.4e6);
            var main = new MainViewModel(Settings.Load(path), [rx], MemoryStore.Load(db));
            Assert.All(main.GroupRows, g => Assert.True(g.Selected));
            Assert.Equal("All groups", main.GroupsText);
            Assert.Equal(2, main.Memories.Count);

            main.ClearGroupsCommand.Execute(null);
            Assert.All(main.GroupRows, g => Assert.False(g.Selected));
            Assert.Equal("No groups", main.GroupsText);
            Assert.Empty(main.Memories);
            Assert.Empty(main.ActiveFrequencies);
            main.Shutdown();      // saves
            var kept = Settings.Load(path).Entry("A");
            Assert.True(kept.NoGroups);
            Assert.Empty(kept.Groups!);

            // The next run keeps none ticked; one ticked is that one only; All groups ticks them all.
            using var rx2 = new Receiver(new FakeSource { Serial = "A" }, playAudio: false, sampleRate: 2.4e6);
            var next = new MainViewModel(Settings.Load(path), [rx2], MemoryStore.Load(db));
            Assert.Empty(next.Memories);
            next.GroupRows.First(g => g.Name == "Fire").Selected = true;
            Assert.Equal("Fire 1", Assert.Single(next.Memories).Channel.Name);
            next.ShowAllGroupsCommand.Execute(null);
            Assert.All(next.GroupRows, g => Assert.True(g.Selected));
            Assert.Equal(2, next.Memories.Count);
            next.Shutdown();
            Assert.False(Settings.Load(path).Entry("A").NoGroups);
        }
        finally
        {
            File.Delete(path);
            File.Delete(db);
        }
    }
}
