// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.ComponentModel;
using System.Diagnostics;
using SimpleScanner.Dsp;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Tests;

/// <summary>
/// Multi Monitor as the window has it: each radio's switch, the one hang time for all, and the four rows a
/// radio's display shows during a scan in the mode. A view model over a real receiver and the pretend radio.
/// </summary>
public class MultiMonitorViewModelTests
{
    // Channels of one window of the pretend radio (1.92 MHz wide), as in MultiMonitorTests.
    private const double A = 121.6e6, B = 121.8e6, C = 121.5e6, D = 121.9e6;

    private static readonly MemoryChannel Alpha = new() { Name = "Alpha", FrequencyHz = A, Agency = "Manchester", Description = "Tower" };
    private static readonly MemoryChannel Bravo = new() { Name = "Bravo", FrequencyHz = B };
    private static readonly MemoryChannel Charlie = new() { Name = "Charlie", FrequencyHz = C };
    private static readonly MemoryChannel Delta = new() { Name = "Delta", FrequencyHz = D };
    // Stored on Alpha's frequency, but not in the groups scanned: a scan does not show it.
    private static readonly MemoryChannel Elsewhere = new() { Name = "Elsewhere", FrequencyHz = A, Agency = "Another town" };

    private static List<MemoryChannel> Members => [Alpha, Bravo, Charlie, Delta];
    private static ScanChannel[] List =>
    [
        new("Alpha", A, Mode.Am, BandPlan.AmAir), new("Bravo", B, Mode.Am, BandPlan.AmAir),
        new("Charlie", C, Mode.Am, BandPlan.AmAir), new("Delta", D, Mode.Am, BandPlan.AmAir),
    ];

    private static MemoryStore Store(params MemoryChannel[] channels)
    {
        var store = new MemoryStore(Path.Combine(Path.GetTempPath(), $"ss-mm-{Guid.NewGuid():N}.db"));
        store.Channels.AddRange(channels);
        return store;
    }

    private static RadioViewModel Make(FakeSource source, RadioSettings settings, MemoryStore store, out Receiver rx)
    {
        rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6) { ScanHangSeconds = 0.2 };
        return new RadioViewModel(rx, settings, store, post: a => a());
    }

    private static bool Wait(Func<bool> condition, double seconds, Action? each = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            each?.Invoke();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    /// <summary>The rows in a word: the title of the channel each holds, or a dash.</summary>
    private static string Rows(RadioViewModel vm) => string.Join(" ", vm.Slots.Select(s => s.Holding ? s.Title : "-"));

    /// <summary>A radio switched on and scanning the four stored channels with the mode on, two carriers up.</summary>
    private static async Task<(RadioViewModel Vm, Receiver Rx, FakeSource Source, FakeCarrier A, FakeCarrier B)> Scanning(bool multi = true)
    {
        var source = new FakeSource();
        var a = source.Add(A, 0.05);
        var b = source.Add(B, 0.03);
        var vm = Make(source, new RadioSettings { SquelchDb = -60, MultiMonitor = multi }, Store(Alpha, Bravo, Charlie, Delta, Elsewhere), out var rx);
        await vm.TogglePowerCommand.ExecuteAsync(null);
        vm.StartScan(Members, List);
        return (vm, rx, source, a, b);
    }

    [Fact]
    public async Task EachRadioHasItsOwnSwitchKeptWithItsSettings()
    {
        var store = Store();
        var a = Make(new FakeSource { Serial = "A" }, new RadioSettings(), store, out var rxA);
        var b = Make(new FakeSource { Serial = "B" }, new RadioSettings(), store, out var rxB);
        using (rxA)
        using (rxB)
        {
            // Off to begin with.
            Assert.False(a.MultiMonitor);
            Assert.False(rxA.MultiMonitor);
            Assert.False(new RadioSettings().MultiMonitor);

            a.Dirty = false;
            a.MultiMonitor = true;
            Assert.True(rxA.MultiMonitor);
            Assert.True(a.Dirty);
            Assert.False(b.MultiMonitor);
            Assert.False(rxB.MultiMonitor);

            var keptA = new RadioSettings();
            var keptB = new RadioSettings();
            a.WriteTo(keptA);
            b.WriteTo(keptB);
            Assert.True(keptA.MultiMonitor);
            Assert.False(keptB.MultiMonitor);

            // Through the file and back, as the app keeps it by serial number.
            string path = Path.Combine(Path.GetTempPath(), $"ss-mm-settings-{Guid.NewGuid():N}.json");
            try
            {
                var file = Settings.Load(path);
                file.Radios["A"] = keptA;
                file.Radios["B"] = keptB;
                file.Save();
                var again = Settings.Load(path);
                Assert.True(again.Radios["A"].MultiMonitor);
                Assert.False(again.Radios["B"].MultiMonitor);

                var a2 = Make(new FakeSource { Serial = "A" }, again.Radios["A"], store, out var rxA2);
                using (rxA2)
                {
                    Assert.True(a2.MultiMonitor);
                    Assert.True(rxA2.MultiMonitor);
                    // Not scanning: the display is as without the mode.
                    Assert.False(a2.ShowSlots);
                }
            }
            finally { File.Delete(path); }

            a.MultiMonitor = false;
            Assert.False(rxA.MultiMonitor);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public void TheHangTimeIsOneForEveryRadioAndIsKept()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ss-mm-settings-{Guid.NewGuid():N}.json");
        string db = Path.Combine(Path.GetTempPath(), $"ss-mm-{Guid.NewGuid():N}.db");
        try
        {
            var settings = Settings.Load(path);
            Assert.Equal(8, settings.MultiHangSeconds);       // to begin with
            settings.MultiHangSeconds = 5;
            static Receiver Rx(string serial) => new(new FakeSource { Serial = serial }, playAudio: false, sampleRate: 2.4e6) { MultiHangSeconds = 99 };
            var receivers = new[] { Rx("A"), Rx("B") };
            var main = new MainViewModel(settings, receivers, new MemoryStore(db));
            Assert.Equal([2.0, 5, 8, 10, 15], main.MultiHangTimes.Select(c => c.Value));
            Assert.Equal(5, main.SelectedMultiHangTime.Value);
            Assert.All(receivers, rx => Assert.Equal(5, rx.MultiHangSeconds));

            main.SelectedMultiHangTime = main.MultiHangTimes.First(c => c.Value == 15);
            Assert.All(receivers, rx => Assert.Equal(15, rx.MultiHangSeconds));
            main.Shutdown();      // saves
            Assert.Equal(15, Settings.Load(path).MultiHangSeconds);

            // The next run, with a radio more: each has it, the one added too.
            var more = new[] { Rx("A"), Rx("B"), Rx("C") };
            var next = new MainViewModel(Settings.Load(path), more, new MemoryStore(db));
            Assert.Equal(15, next.SelectedMultiHangTime.Value);
            Assert.All(more, rx => Assert.Equal(15, rx.MultiHangSeconds));
            next.Shutdown();

            // A value not offered takes the usual one.
            var odd = Settings.Load(path);
            odd.MultiHangSeconds = 7;
            var rx1 = Rx("A");
            var third = new MainViewModel(odd, [rx1], new MemoryStore(db));
            Assert.Equal(8, third.SelectedMultiHangTime.Value);
            Assert.Equal(8, rx1.MultiHangSeconds);
            third.Shutdown();
        }
        finally
        {
            File.Delete(path);
            File.Delete(db);
        }
    }

    [Fact]
    public async Task TwoCarriersInAWindowShowAsTwoRowsWithTheScansOwnNames()
    {
        var (vm, rx, _, _, _) = await Scanning();
        using (rx)
        {
            Assert.True(vm.ShowSlots);
            Assert.False(vm.CanAvoidChannel);
            Assert.Equal(4, vm.Slots.Count);
            Assert.Equal([0, 1, 2, 3], vm.Slots.Select(s => s.Seat));
            Assert.Equal(["33% L", "33% R", "Hard L", "Hard R"], vm.Slots.Select(s => s.SeatLabel));
            var rows = vm.Slots.ToArray();

            Assert.True(Wait(() => Rows(vm) == "Alpha Bravo - -", 3, vm.Tick), Rows(vm));
            Assert.True(Wait(() => vm.Slots[0].SquelchOpen && vm.Slots[1].SquelchOpen, 1, vm.Tick));
            Assert.Equal(rows, vm.Slots);                // the same four rows
            var (one, two) = (vm.Slots[0], vm.Slots[1]);
            Assert.Equal(A, one.FrequencyHz);
            Assert.Equal("121.6000", one.FrequencyText);
            // The scan's own channel speaks for the frequency, not the other one stored there.
            Assert.Equal("Alpha", one.Title);
            Assert.Equal("Manchester: Tower", one.DetailText);
            Assert.Equal(B, two.FrequencyHz);
            Assert.Equal("Bravo", two.Title);
            Assert.Equal("AM 25k", one.ModeText);
            Assert.Equal("", one.DigitalText);
            Assert.False(one.IsEncrypted);
            Assert.InRange(one.Level, -32, -20);
            Assert.Equal($"{one.Level:F0} dB", one.LevelText);
            Assert.False(one.Waiting || two.Waiting);
            Assert.True(one.SkipCommand.CanExecute(null));
            foreach (var empty in vm.Slots.Skip(2))
            {
                Assert.False(empty.Holding);
                Assert.Equal("", empty.Title);
                Assert.Equal(0, empty.FrequencyHz);
                Assert.False(empty.SkipCommand.CanExecute(null));
                Assert.False(empty.AvoidCommand.CanExecute(null));
            }
            Assert.Equal("MULTI · 2 of 4 · " + BandPlan.Find(A).Name, vm.SlotsHeader);
            // The main display speaks for the first row, as without the mode.
            Assert.Equal(A, vm.FrequencyHz);
            Assert.Equal("Alpha", vm.BandName);

            // Twenty times a second: a row says again only what has changed.
            var said = new List<string>();
            void Heard(object? o, PropertyChangedEventArgs e) => said.Add(e.PropertyName ?? "");
            foreach (var row in vm.Slots) row.PropertyChanged += Heard;
            vm.PropertyChanged += Heard;
            for (int k = 0; k < 20; k++)
            {
                vm.Tick();
                Thread.Sleep(10);
            }
            Assert.Empty(said.Except([nameof(SlotViewModel.Level), nameof(SlotViewModel.LevelText), nameof(RadioViewModel.Level),
                nameof(RadioViewModel.LevelText), nameof(RadioViewModel.DigitalText), nameof(RadioViewModel.GainText)]));
            foreach (var row in vm.Slots) row.PropertyChanged -= Heard;
            vm.PropertyChanged -= Heard;
            vm.StopScan();
        }
    }

    [Fact]
    public async Task ARowWhoseSignalHasGoneWaitsForTheHangTimeThenEmpties()
    {
        var (vm, rx, _, _, b) = await Scanning();
        using (rx)
        {
            vm.SetMultiHangTime(new Choice("2s", 2));
            Assert.Equal(2, rx.MultiHangSeconds);
            Assert.True(Wait(() => Rows(vm) == "Alpha Bravo - -" && vm.Slots[1].SquelchOpen, 3, vm.Tick), Rows(vm));

            b.On = false;
            Assert.True(Wait(() => vm.Slots[1].Waiting, 1.5, vm.Tick), "the row did not say it was waiting");
            var sw = Stopwatch.StartNew();
            Assert.True(vm.Slots[1].Holding);
            Assert.False(vm.Slots[1].SquelchOpen);
            Assert.Equal("Bravo", vm.Slots[1].Title);
            Assert.Equal("MULTI · 2 of 4 · " + BandPlan.Find(A).Name, vm.SlotsHeader);

            // Wide bounds: the squelch shuts a little after the carrier, and a busy machine may be late to look.
            Assert.True(Wait(() => !vm.Slots[1].Holding, 5, vm.Tick), "the row was not let go");
            Assert.InRange(sw.Elapsed.TotalSeconds, 1.0, 3.5);
            Assert.False(vm.Slots[1].Waiting);
            Assert.Equal("", vm.Slots[1].Title);
            Assert.Equal("Alpha - - -", Rows(vm));
            Assert.Equal("MULTI · 1 of 4 · " + BandPlan.Find(A).Name, vm.SlotsHeader);
            vm.StopScan();
        }
    }

    [Fact]
    public async Task ARowsSkipLetsGoOnlyThatRow()
    {
        var (vm, rx, _, _, _) = await Scanning();
        using (rx)
        {
            Assert.True(Wait(() => Rows(vm) == "Alpha Bravo - -", 3, vm.Tick), Rows(vm));
            vm.Slots[1].SkipCommand.Execute(null);
            Assert.True(Wait(() => Rows(vm) == "Alpha - - -", 1, vm.Tick), Rows(vm));
            // Passed by: it does not come back while the other is held.
            Thread.Sleep(500);
            vm.Tick();
            Assert.Equal("Alpha - - -", Rows(vm));
            Assert.Equal(0, rx.AvoidedCount);
            Assert.True(vm.IsScanning);
            vm.StopScan();
        }
    }

    [Fact]
    public async Task ARowsAvoidLetsItGoAndCountsIt()
    {
        var (vm, rx, _, _, _) = await Scanning();
        using (rx)
        {
            Assert.True(Wait(() => Rows(vm) == "Alpha Bravo - -", 3, vm.Tick), Rows(vm));
            Assert.Equal(0, rx.AvoidedCount);
            vm.Slots[0].AvoidCommand.Execute(null);
            Assert.Equal(1, rx.AvoidedCount);
            Assert.True(Wait(() => Rows(vm) == "- Bravo - -", 1, vm.Tick), Rows(vm));
            Thread.Sleep(500);
            vm.Tick();
            Assert.Equal("- Bravo - -", Rows(vm));
            vm.StopScan();
        }
    }

    [Fact]
    public async Task TheMainAvoidIsNotThereInTheMode()
    {
        var (vm, rx, _, _, _) = await Scanning();
        using (rx)
        {
            Assert.True(Wait(() => Rows(vm) == "Alpha Bravo - -", 3, vm.Tick), Rows(vm));
            Assert.False(vm.CanAvoidChannel);
            Assert.False(vm.AvoidChannelCommand.CanExecute(null));

            // The mode switched off during the scan: the main Avoid is back, and the display as before.
            var said = new List<string>();
            vm.PropertyChanged += (_, e) => said.Add(e.PropertyName ?? "");
            vm.MultiMonitor = false;
            Assert.Contains(nameof(RadioViewModel.CanAvoidChannel), said);
            Assert.Contains(nameof(RadioViewModel.ShowSlots), said);
            Assert.True(vm.CanAvoidChannel);
            Assert.True(vm.AvoidChannelCommand.CanExecute(null));
            Assert.False(vm.ShowSlots);
            Assert.Equal("- - - -", Rows(vm));
            Assert.Equal("", vm.SlotsHeader);

            vm.StopScan();
            Assert.False(vm.CanAvoidChannel);        // nothing to avoid outside a scan, as the button has it now
        }
    }

    [Fact]
    public async Task WithTheModeOffOrTheScanStoppedTheDisplayIsAsBefore()
    {
        // The mode off: one channel, the big display, no rows.
        var (off, rxOff, _, _, _) = await Scanning(multi: false);
        using (rxOff)
        {
            Assert.False(off.ShowSlots);
            Assert.True(off.CanAvoidChannel);
            Assert.True(Wait(() => off.BandName == "Alpha", 3, off.Tick), off.BandName);
            Assert.Equal(A, off.FrequencyHz);
            Assert.Equal("Manchester: Tower", off.DetailText);
            Assert.Equal("AM 25k", off.ModeText);
            for (int k = 0; k < 5; k++) off.Tick();
            Assert.Equal("- - - -", Rows(off));
            Assert.Equal("", off.SlotsHeader);
            off.StopScan();
        }

        // The mode on, the scan stopped: the rows go and the display shows where the receiver is.
        var (on, rx, _, _, _) = await Scanning();
        using (rx)
        {
            Assert.True(Wait(() => Rows(on) == "Alpha Bravo - -", 3, on.Tick), Rows(on));
            on.StopScan();
            Assert.False(on.ShowSlots);
            Assert.Equal("- - - -", Rows(on));
            Assert.Equal("", on.SlotsHeader);
            for (int k = 0; k < 5; k++) on.Tick();
            Assert.Equal("- - - -", Rows(on));
            Assert.Equal(rx.FrequencyHz, on.FrequencyHz);
            Assert.Equal(A, on.FrequencyHz);
            // Outside a scan every stored channel speaks for the frequency.
            Assert.Equal("Alpha / Elsewhere", on.BandName);
            Assert.Equal("AM 25k", on.ModeText);
            Assert.Equal(new[] { 0.0, 0, 0, 0 }, on.Slots.Select(s => s.FrequencyHz));
        }
    }

    [Fact]
    public async Task ASearchInTheModeNamesRowsFromEveryStoredChannel()
    {
        var source = new FakeSource();
        source.Add(A, 0.05);
        source.Add(B, 0.03);
        var vm = Make(source, new RadioSettings { SquelchDb = -60, MultiMonitor = true }, Store(Alpha, Bravo, Elsewhere), out var rx);
        using (rx)
        {
            await vm.TogglePowerCommand.ExecuteAsync(null);
            vm.StartSearch([new("", C, Mode.Am, BandPlan.AmAir), new("", A, Mode.Am, BandPlan.AmAir), new("", B, Mode.Am, BandPlan.AmAir),
                new("", D, Mode.Am, BandPlan.AmAir), new("", 121.7e6, Mode.Am, BandPlan.AmAir)]);
            Assert.True(vm.ShowSlots);
            vm.Tick();
            Assert.StartsWith("MULTI · 0 of 4 · Searching", vm.SlotsHeader);
            Assert.True(Wait(() => Rows(vm) == "Alpha / Elsewhere Bravo - -", 3, vm.Tick), Rows(vm));
            Assert.Equal("Manchester: Tower  ·  Another town", vm.Slots[0].DetailText);
            vm.StopScan();
            Assert.False(vm.ShowSlots);
        }
    }
}
