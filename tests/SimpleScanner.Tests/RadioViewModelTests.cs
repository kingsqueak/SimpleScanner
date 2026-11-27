// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Text.Json;
using SimpleScanner.Dsp;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Tests;

/// <summary>Each radio's settings by serial number, and a file from before there were several radios.</summary>
public class RadioSettingsTests
{
    // As a build with one radio wrote it.
    private const string OneRadioFile = """
        {
          "Mini": false,
          "FrequencyHz": 104900000,
          "AutoMode": false,
          "Mode": 2,
          "BandwidthHz": 200000,
          "StepHz": 12500,
          "Volume": 0.25,
          "SquelchDb": -109.5,
          "SquelchOn": true,
          "AutoSquelch": true,
          "Stereo": false,
          "AutoGain": false,
          "GainStep": 4,
          "RadioSerial": "2001068442",
          "Antenna": "Antenna B",
          "BiasT": true,
          "FmNotch": true,
          "Ppm": 1.5,
          "LevelOffsetDb": 2,
          "Group": null,
          "Groups": [ "Manchester (KMHT)", "Tag: Aircraft" ],
          "AudioDevice": "Patch Desktop",
          "AvoidMinutes": 60,
          "ResumeSeconds": 15,
          "SearchFromMHz": 144,
          "SearchToMHz": 149
        }
        """;

    private static string TempFile(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ss-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void AFileFromOneRadioBecomesThatRadiosSettings()
    {
        string path = TempFile(OneRadioFile);
        try
        {
            var s = Settings.Load(path);
            var r = Assert.Single(s.Radios).Value;
            Assert.Equal("2001068442", s.Radios.Keys.Single());
            Assert.False(r.Use);                       // nothing is run that was not asked for
            Assert.Equal(104.9e6, r.FrequencyHz);
            Assert.False(r.AutoMode);
            Assert.Equal(Mode.Wfm, r.Mode);
            Assert.Equal(200000, r.BandwidthHz);
            Assert.Equal(12500, r.StepHz);
            Assert.Equal(0.25, r.Volume);
            Assert.Equal(0, r.Pan);
            Assert.Equal(-109.5, r.SquelchDb);
            Assert.True(r.SquelchOn);
            Assert.True(r.AutoSquelch);
            Assert.False(r.Stereo);
            Assert.False(r.AutoGain);
            Assert.Equal(4, r.GainStep);
            Assert.Equal("Antenna B", r.Antenna);
            Assert.True(r.BiasT);
            Assert.True(r.FmNotch);
            Assert.Equal(1.5, r.Ppm);
            Assert.Equal(2, r.LevelOffsetDb);
            Assert.Empty(s.GroupRadio);
            // What is shared stays where it was.
            Assert.Equal(["Manchester (KMHT)", "Tag: Aircraft"], s.Groups);
            Assert.Equal("Patch Desktop", s.AudioDevice);
            Assert.Equal(60, s.AvoidMinutes);

            // Saved and read again: the same, and not lifted a second time over what the radio now has.
            r.FrequencyHz = 162.4e6;
            s.Radios["0000000001"] = new RadioSettings { Use = true, Pan = 1, FrequencyHz = 162.55e6 };
            s.GroupRadio["Manchester (KMHT)"] = "0000000001";
            s.Save();
            var again = Settings.Load(path);
            Assert.Equal(2, again.Radios.Count);
            Assert.Equal(162.4e6, again.Radios["2001068442"].FrequencyHz);
            Assert.True(again.Radios["0000000001"].Use);
            Assert.Equal(1, again.Radios["0000000001"].Pan);
            Assert.Equal("0000000001", again.GroupRadio["Manchester (KMHT)"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ABuildFromBeforeCanStillReadTheFile()
    {
        string path = TempFile(OneRadioFile);
        try
        {
            var s = Settings.Load(path);
            // The window's own radio has moved on; what it has goes in the one-radio fields as well.
            var mine = s.Radios["2001068442"];
            mine.FrequencyHz = 155.7e6;
            mine.SquelchDb = -95;
            mine.Antenna = "Antenna C";
            s.SetFlat(mine);
            s.Save();

            // A build from before reads the fields it knows, by name, and passes over the rest.
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal(155.7e6, root.GetProperty("FrequencyHz").GetDouble());
            Assert.Equal(-95, root.GetProperty("SquelchDb").GetDouble());
            Assert.Equal("Antenna C", root.GetProperty("Antenna").GetString());
            Assert.Equal("2001068442", root.GetProperty("RadioSerial").GetString());
            Assert.Equal(2, root.GetProperty("Mode").GetInt32());
            Assert.True(root.GetProperty("BiasT").GetBoolean());
            Assert.Equal(2, root.GetProperty("Groups").GetArrayLength());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("Radios").ValueKind);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WithNoRadioChosenYetNothingIsLiftedAndTheFirstRadioTakesWhatWasKept()
    {
        string path = TempFile("""{ "FrequencyHz": 121500000, "Volume": 0.75, "Radios": null }""");
        try
        {
            var s = Settings.Load(path);
            Assert.Empty(s.Radios);
            Assert.Empty(s.GroupRadio);
            Assert.Equal(121.5e6, s.ForFirstRadio(null).FrequencyHz);
            Assert.Equal(0.75, s.ForFirstRadio("123").Volume);       // a radio not seen before: as it was left
            s.Entry("123").Volume = 0.1;
            Assert.Equal(0.1, s.ForFirstRadio("123").Volume);        // one seen before: its own
            // A radio run beside the first starts from the usual settings, not from the first one's.
            Assert.Equal(new RadioSettings().FrequencyHz, s.Entry("456").FrequencyHz);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AMissingOrBrokenFileGivesTheUsualSettings()
    {
        var s = Settings.Load(Path.Combine(Path.GetTempPath(), $"ss-none-{Guid.NewGuid():N}.json"));
        Assert.Empty(s.Radios);
        Assert.Equal(162.4e6, s.FrequencyHz);
        string path = TempFile("{ not json");
        try { Assert.Empty(Settings.Load(path).Radios); }
        finally { File.Delete(path); }
    }
}

/// <summary>One radio's part of the front panel, over a pretend radio and with no window.</summary>
public class RadioViewModelTests
{
    private static bool Wait(Func<bool> condition, double seconds, Action? each = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            each?.Invoke();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    private static MemoryStore Store(params MemoryChannel[] channels)
    {
        var store = new MemoryStore(Path.Combine(Path.GetTempPath(), $"ss-radio-{Guid.NewGuid():N}.db"));
        store.Channels.AddRange(channels);
        return store;
    }

    private static RadioViewModel Make(FakeSource source, RadioSettings settings, MemoryStore store, out Receiver rx)
    {
        rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6) { ScanHangSeconds = 0.2 };
        return new RadioViewModel(rx, settings, store, post: a => a());
    }

    [Fact]
    public void ItTakesItsSettingsSetsTheReceiverAndGivesThemBack()
    {
        var source = new FakeSource();
        var kept = new RadioSettings
        {
            FrequencyHz = 121.5e6, AutoMode = false, Mode = Mode.Am, BandwidthHz = BandPlan.AmAir833, StepHz = 25000,
            Volume = 0.3, Pan = -1, SquelchDb = -80, AutoSquelch = true, Stereo = false, AutoGain = false, GainStep = 3,
            BiasT = true, FmNotch = true, Ppm = 2.5, LevelOffsetDb = 4, Use = true, Antenna = "Antenna B",
        };
        var vm = Make(source, kept, Store(), out var rx);
        using (rx)
        {
            Assert.Equal(121.5e6, vm.FrequencyHz);
            Assert.Equal(121.5e6, rx.FrequencyHz);
            Assert.Equal(Mode.Am, rx.Mode);
            Assert.Equal(BandPlan.AmAir833, rx.BandwidthHz);
            Assert.Equal("AM 8.33k", vm.ModeText);
            Assert.True(vm.IsAm);
            Assert.Equal("121.5", vm.EntryText);
            Assert.Equal(0.3, rx.Volume, 6);
            Assert.Equal(-1, rx.Pan);
            Assert.Equal(-80, rx.SquelchDb);
            Assert.True(rx.SquelchEnabled);
            Assert.True(rx.AutoSquelch);
            Assert.False(rx.StereoEnabled);
            Assert.Equal(4, rx.LevelOffsetDb);
            Assert.False(source.AutoGain);
            Assert.Equal(3, source.GainStep);
            Assert.True(source.BiasT);
            Assert.True(source.FmNotch);
            Assert.Equal(2.5, source.Ppm);
            Assert.Equal("Antenna", source.Antenna);       // the pretend radio has one socket

            vm.Dirty = false;
            vm.SquelchDb = -70;
            vm.Volume = 60;
            vm.GainStep = 5;
            Assert.True(vm.Dirty);
            Assert.Equal(-70, rx.SquelchDb);
            Assert.Equal(5, source.GainStep);
            vm.ToggleMuteCommand.Execute(null);
            Assert.Equal(0, rx.Volume);
            vm.ToggleMuteCommand.Execute(null);
            Assert.Equal(0.6, rx.Volume, 6);

            // The pan goes to the receiver; a touch either side of the middle is the middle.
            vm.Dirty = false;
            vm.Pan = 0.5;
            Assert.Equal(0.5, rx.Pan);
            Assert.True(vm.Dirty);
            vm.Pan = 0.03;
            Assert.Equal(0, vm.Pan);
            Assert.Equal(0, rx.Pan);
            vm.Pan = -7;
            Assert.Equal(-1, vm.Pan);
            Assert.Equal(-1, rx.Pan);

            var back = new RadioSettings { Antenna = "Antenna B", Use = true };
            vm.WriteTo(back);
            Assert.Equal(121.5e6, back.FrequencyHz);
            Assert.False(back.AutoMode);
            Assert.Equal(Mode.Am, back.Mode);
            Assert.Equal(BandPlan.AmAir833, back.BandwidthHz);
            Assert.Equal(25000, back.StepHz);
            Assert.Equal(0.6, back.Volume, 6);
            Assert.Equal(-1, back.Pan);
            Assert.Equal(-70, back.SquelchDb);
            Assert.True(back.SquelchOn);
            Assert.True(back.AutoSquelch);
            Assert.False(back.Stereo);
            Assert.False(back.AutoGain);
            Assert.Equal(5, back.GainStep);
            Assert.True(back.BiasT);
            Assert.True(back.FmNotch);
            Assert.Equal(2.5, back.Ppm);
            Assert.Equal(4, back.LevelOffsetDb);
            Assert.Equal("Antenna B", back.Antenna);       // a radio with one socket leaves the choice kept for one with several
            Assert.True(back.Use);
        }
    }

    [Fact]
    public void TuningByEntryStepAndStoredChannel()
    {
        var tower = new MemoryChannel { Name = "Tower", FrequencyHz = 121.3e6, Agency = "Manchester", Description = "Tower" };
        var fire = new MemoryChannel { Name = "Fire", FrequencyHz = 154.43e6, Mode = Mode.Nfm, BandwidthHz = BandPlan.NfmNarrow };
        var store = Store(tower, fire);
        var vm = Make(new FakeSource(), new RadioSettings(), store, out var rx);
        using (rx)
        {
            Assert.Equal(162.4e6, vm.FrequencyHz);
            Assert.True(vm.AutoMode);

            vm.EntryText = "121.3";
            vm.EnterFrequencyCommand.Execute(null);
            Assert.Equal(121.3e6, rx.FrequencyHz);
            Assert.Equal(Mode.Am, rx.Mode);                // the band plan's mode for the airband
            Assert.Equal("Tower", vm.BandName);
            Assert.Contains("Manchester", vm.DetailText);

            vm.EntryText = "nonsense";
            vm.EnterFrequencyCommand.Execute(null);
            Assert.Contains("Enter a frequency", vm.Status);
            Assert.Equal(121.3e6, vm.FrequencyHz);

            vm.SelectedStep = vm.Steps.First(s => s.Value == 25000);
            vm.StepUpCommand.Execute(null);
            Assert.Equal(121.325e6, vm.FrequencyHz);
            Assert.NotEqual("Tower", vm.BandName);
            vm.StepDownCommand.Execute(null);
            Assert.Equal(121.3e6, vm.FrequencyHz);

            vm.Tune(fire);
            Assert.Same(fire, vm.Picked);
            Assert.Equal(154.43e6, rx.FrequencyHz);
            Assert.False(vm.AutoMode);
            Assert.Equal(Mode.Nfm, rx.Mode);
            Assert.Equal(BandPlan.NfmNarrow, rx.BandwidthHz);
            Assert.Equal("NFM 12.5k", vm.ModeText);
            Assert.Equal("Fire", vm.BandName);

            // The stored channels change under it: the display follows.
            fire.Name = "Fire dispatch";
            vm.StoreChanged();
            Assert.Equal("Fire dispatch", vm.BandName);
        }
    }

    [Fact]
    public async Task PowerScanAndTheDisplay()
    {
        var source = new FakeSource();
        var one = new MemoryChannel { Name = "One", FrequencyHz = 120.0e6 };
        var three = new MemoryChannel { Name = "Three", FrequencyHz = 125.0e6, Agency = "Somewhere" };
        var store = Store(one, three);
        var vm = Make(source, new RadioSettings { SquelchDb = -60 }, store, out var rx);
        using (rx)
        {
            List<MemoryChannel> members = [one, three];
            ScanChannel[] list = [new("One", 120.0e6, Mode.Am, BandPlan.AmAir), new("Three", 125.0e6, Mode.Am, BandPlan.AmAir)];

            vm.StartScan(members, list);
            Assert.Equal("Start the radio first", vm.Status);
            Assert.False(vm.IsScanning);

            await vm.TogglePowerCommand.ExecuteAsync(null);
            Assert.True(vm.IsOn);
            Assert.Equal("fake", vm.DeviceText);
            Assert.Equal("Stop", vm.PowerText);
            Assert.Equal("", vm.Status);

            vm.StartScan(members, list);
            Assert.True(vm.IsScanning);
            Assert.Equal("Stop", vm.ScanText);
            vm.Tick();
            Assert.StartsWith("Scanning", vm.BandName);

            source.CarrierHz = 125.0e6;
            Assert.True(Wait(() => vm.BandName == "Three", 3, vm.Tick), $"the display says \"{vm.BandName}\"");
            Assert.Equal(125.0e6, vm.FrequencyHz);
            Assert.Contains("Somewhere", vm.DetailText);
            Assert.True(Wait(() => vm.SquelchOpen, 1, vm.Tick));
            Assert.True(vm.IsScanning);

            vm.StopScan();
            Assert.False(vm.IsScanning);
            Assert.False(rx.Scanning);
            Assert.Equal("Scan", vm.ScanText);

            // A search is a scan that names nothing.
            source.CarrierHz = 0;
            vm.StartSearch(list);
            Assert.True(vm.IsSearching);
            Assert.Equal("Stop search", vm.SearchText);
            vm.Tick();
            Assert.StartsWith("Searching", vm.BandName);
            vm.StopScan();
            Assert.False(vm.IsSearching);

            await vm.TogglePowerCommand.ExecuteAsync(null);
            Assert.False(vm.IsOn);
            Assert.Equal("Off", vm.DeviceText);
            vm.Tick();
            Assert.Equal(-140, vm.Level);
        }
    }

    // Reported 2026-11-27: with Auto gain off and the step at 6 (a preamp at the antenna), scanning
    // changed it. The gain slider's maximum followed the band, and the slider pulled its value down
    // to a lower one and wrote that back as the setting. The maximum is now the model's.
    [Fact]
    public async Task ScanningThroughABandWithFewerStepsLeavesTheGainSettingAlone()
    {
        var source = new FakeSource { MostGainSteps = 8, MaxStepAt = hz => hz < 420e6 ? 8 : 5 };   // as an RSP2
        var vhf = new MemoryChannel { Name = "VHF", FrequencyHz = 155.0e6 };
        var uhf = new MemoryChannel { Name = "UHF", FrequencyHz = 460.0e6 };
        var vm = Make(source, new RadioSettings { AutoGain = false, GainStep = 6, SquelchDb = -60 }, Store(vhf, uhf), out var rx);
        using (rx)
        {
            await vm.TogglePowerCommand.ExecuteAsync(null);
            vm.StartScan([vhf, uhf], [new("VHF", 155.0e6, Mode.Nfm, BandPlan.NfmNarrow), new("UHF", 460.0e6, Mode.Nfm, BandPlan.NfmNarrow)]);
            bool sawUhf = false, sawVhf = false;
            Assert.True(Wait(() =>
            {
                vm.Tick();
                Assert.Equal(8, vm.MaxGainStep);
                if (source.CenterHz > 420e6) sawUhf = true;
                else if (sawUhf) sawVhf = true;
                return sawUhf && sawVhf;
            }, 5), "the scan did not go from UHF back to VHF");
            Assert.Equal(6, vm.GainStep);
            Assert.Equal(6, source.GainStep);
            var back = new RadioSettings();
            vm.WriteTo(back);
            Assert.Equal(6, back.GainStep);
            Assert.False(back.AutoGain);
        }
    }

    [Fact]
    public async Task ARadioThatWillNotStartSaysWhy()
    {
        var source = new FakeSource { FailToStart = "No radio found." };
        var vm = Make(source, new RadioSettings(), Store(), out var rx);
        using (rx)
        {
            await vm.TogglePowerCommand.ExecuteAsync(null);
            Assert.False(vm.IsOn);
            Assert.False(vm.IsBusy);
            Assert.Equal("No radio found.", vm.Status);
            Assert.Equal("Off", vm.DeviceText);
        }
    }

    [Fact]
    public async Task TwoRadiosKeepToThemselves()
    {
        var store = Store(new MemoryChannel { Name = "Weather", FrequencyHz = 162.4e6 });
        var a = Make(new FakeSource { Serial = "A" }, new RadioSettings { FrequencyHz = 121.5e6, SquelchDb = -60 }, store, out var rxA);
        var b = Make(new FakeSource { Serial = "B" }, new RadioSettings { FrequencyHz = 162.4e6, Volume = 0.2 }, store, out var rxB);
        using (rxA)
        using (rxB)
        {
            Assert.Equal("A", a.Serial);
            Assert.Equal("B", b.Serial);
            await a.TogglePowerCommand.ExecuteAsync(null);
            Assert.True(a.IsOn);
            Assert.False(b.IsOn);
            Assert.Equal("Weather", b.BandName);
            Assert.NotEqual("Weather", a.BandName);

            a.FrequencyHz = 127.35e6;
            a.Volume = 90;
            Assert.Equal(162.4e6, rxB.FrequencyHz);
            Assert.Equal(0.2, rxB.Volume, 6);
            Assert.Equal(127.35e6, rxA.FrequencyHz);

            // The shared timers are given to each.
            var hour = new Choice("1 hr", 60);
            a.SetAvoidTime(hour);
            b.SetAvoidTime(new Choice("Full session", 0));
            a.SetResumeTime(new Choice("15s", 15));
            Assert.Equal(TimeSpan.FromMinutes(60), rxA.AvoidTime);
            Assert.Null(rxB.AvoidTime);
            Assert.Equal(15, rxA.ResumeSeconds);
            await a.TogglePowerCommand.ExecuteAsync(null);
        }
    }
}
