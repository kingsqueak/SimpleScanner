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
            var vm = new MainViewModel(Settings.Load(), new Receiver(new SdrPlaySource()), MemoryStore.Load());
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => vm.Shutdown();
            // --start: switch the receiver on at launch.
            if ((desktop.Args ?? []).Contains("--start")) vm.TogglePowerCommand.Execute(null);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
