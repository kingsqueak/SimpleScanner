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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = AppFiles.OpenStore();
            FillLocations(store);
            var settings = Settings.Load();
            var source = new SdrPlaySource { Serial = settings.RadioSerial };
            var vm = new MainViewModel(settings, new Receiver(source), store);
            var window = new MainWindow { DataContext = vm };
            window.RestorePlace();
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                window.KeepPlace();
                vm.Shutdown();
            };
            bool start = (desktop.Args ?? []).Contains("--start");
            window.Opened += async (_, _) =>
            {
                await ChooseRadio(window, source, settings);
                vm.RefreshRadios();
                // --start: switch the receiver on at launch.
                if (start) vm.TogglePowerCommand.Execute(null);
            };
        }
        base.OnFrameworkInitializationCompleted();
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
