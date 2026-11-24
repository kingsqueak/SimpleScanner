using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SimpleScanner.Radio.SdrPlay;
using SimpleScanner.ViewModels;
using SimpleScanner.Views;

namespace SimpleScanner;

public partial class App : Application
{
    public override void Initialize()
    {
        // The name macOS shows in the menu bar and in "Hide" and "Quit"; without it, "Avalonia Application".
        Name = AppInfo.Title;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = AppFiles.OpenStore();
            FillLocations(store);
            var settings = Settings.Load();
            // The radios marked for use that are plugged in. The window runs the first itself (the one used
            // last, if it is among them); each other gets a helper program, because the SDRplay software
            // lets one program open one radio only. With none marked it is as it was with one radio.
            var inUse = RadiosInUse(settings);
            var source = new SdrPlaySource { Serial = inUse.Count > 0 ? inUse[0].Serial : settings.RadioSerial };
            var receivers = new List<IReceiver> { new Receiver(source) };
            string trouble = "";
            foreach (var radio in inUse.Skip(1))
            {
                try { receivers.Add(Remote.RemoteReceiver.Launch(radio.Serial, SdrPlaySource.ListRadios)); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    trouble = $"{radio.Label}: {e.Message}";
                }
            }
            var vm = new MainViewModel(settings, receivers, store);
            if (trouble.Length > 0) vm.Radio.Status = trouble;
            var window = new MainWindow { DataContext = vm };
            window.RestorePlace();
            desktop.MainWindow = window;
            // The application's own menu on macOS, in place of "About Avalonia".
            var about = new NativeMenuItem($"About {AppInfo.Name}");
            about.Click += (_, _) => window.OpenHelp("About");
            var help = new NativeMenuItem($"{AppInfo.Name} Help");
            help.Click += (_, _) => window.OpenHelp();
            NativeMenu.SetMenu(this, new NativeMenu { Items = { about, help } });
            desktop.ShutdownRequested += (_, _) =>
            {
                window.KeepPlace();
                vm.Shutdown();      // stops the radios and ends the helper programs
            };
            bool start = (desktop.Args ?? []).Contains("--start");
            window.Opened += async (_, _) =>
            {
                if (inUse.Count == 0) await ChooseRadio(window, source, settings);
                vm.RefreshRadios();
                // --start: switch the receivers on at launch.
                if (start) await vm.StartAll();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The radios marked for use in the settings that are plugged in and free, the one used last first.</summary>
    private static List<Radio.RadioInfo> RadiosInUse(Settings settings)
    {
        if (!settings.Radios.Values.Any(r => r.Use)) return [];
        IReadOnlyList<Radio.RadioInfo> present;
        try { present = SdrPlaySource.ListRadios(); }
        catch (InvalidOperationException) { return []; }    // the radio's software is missing: said when the receiver is switched on
        return present.Where(r => settings.Radios.TryGetValue(r.Serial, out var kept) && kept.Use)
            .OrderBy(r => r.Serial == settings.RadioSerial ? 0 : 1).ToList();
    }

    /// <summary>
    /// With more than one radio plugged in and the one used last time not among them (or none used
    /// yet), ask which to use. Otherwise nothing is shown: the last one is opened, or the source
    /// finds its own. The Receiver box in Settings changes it afterwards.
    /// </summary>
    private static async Task ChooseRadio(Window owner, SdrPlaySource source, Settings settings)
    {
        IReadOnlyList<Radio.RadioInfo> radios;
        try { radios = SdrPlaySource.ListRadios(); }
        catch (InvalidOperationException) { return; }      // said when the receiver is switched on
        if (radios.Count < 2 || radios.Any(r => r.Serial == settings.RadioSerial)) return;
        var chooser = new RadioChooserWindow(radios, settings.RadioSerial);
        await chooser.ShowDialog(owner);
        if (chooser.Chosen is not { } radio) return;
        source.Serial = radio.Serial;
        if (settings.RadioSerial == radio.Serial) return;
        settings.RadioSerial = radio.Serial;
        settings.Save();
    }

    /// <summary>
    /// Channels stored before locations were kept, or from a CSV file, get theirs from what was
    /// fetched from RadioReference before, so the location groups are there without opening its browser.
    /// </summary>
    private static void FillLocations(MemoryStore store)
    {
        if (!store.Channels.Any(c => c.Location is null && c.ImportKey is not null)) return;
        using var cache = new RadioReference.RrCache();
        if (RadioReference.RrPlaces.FillFromCache(store, cache) > 0) store.Save();
    }
}
