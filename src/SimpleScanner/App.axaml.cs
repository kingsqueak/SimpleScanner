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
                // --start: switch the receiver on at launch.
                if (start) vm.TogglePowerCommand.Execute(null);
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// With more than one radio plugged in, ask which to use, offering the one used last time.
    /// With one, or none, or no API to ask, nothing is shown and the source finds its own.
    /// </summary>
    private static async Task ChooseRadio(Window owner, SdrPlaySource source, Settings settings)
    {
        IReadOnlyList<Radio.RadioInfo> radios;
        try { radios = SdrPlaySource.ListRadios(); }
        catch (InvalidOperationException) { return; }      // said when the receiver is switched on
        if (radios.Count < 2) return;
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
