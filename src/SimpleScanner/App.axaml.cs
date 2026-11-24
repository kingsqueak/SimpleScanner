using Avalonia;
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
            var vm = new MainViewModel(Settings.Load(), new Receiver(new SdrPlaySource()), store);
            var window = new MainWindow { DataContext = vm };
            window.RestorePlace();
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                window.KeepPlace();
                vm.Shutdown();
            };
            // --start: switch the receiver on at launch.
            if ((desktop.Args ?? []).Contains("--start")) vm.TogglePowerCommand.Execute(null);
        }
        base.OnFrameworkInitializationCompleted();
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
