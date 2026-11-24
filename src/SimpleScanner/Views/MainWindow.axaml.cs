using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Views;

public partial class MainWindow : Window
{
    private ChannelsWindow? _channels;

    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) => KeepPlace();
        Opened += (_, _) => BringOntoAScreen();
    }

    /// <summary>Put the window where it was when it was last closed, at the size it had.</summary>
    public void RestorePlace()
    {
        if ((DataContext as MainViewModel)?.WindowPlace is not { } p) return;
        Width = Math.Max(p.Width, MinWidth);
        Height = Math.Max(p.Height, MinHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(p.X, p.Y);
    }

    /// <summary>Remember where the window is. A maximised or minimised window keeps the place it had before.</summary>
    public void KeepPlace()
    {
        if (DataContext is not MainViewModel main || WindowState != WindowState.Normal) return;
        if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
        main.WindowPlace = new Settings.Place(Position.X, Position.Y, ClientSize.Width, ClientSize.Height);
    }

    /// <summary>If the screen the window was on has gone, so that its title bar cannot be reached, centre it on the main one.</summary>
    private void BringOntoAScreen()
    {
        if (WindowStartupLocation != WindowStartupLocation.Manual) return;
        var top = new PixelRect(Position.X + 40, Position.Y, Math.Max(1, (int)(ClientSize.Width * RenderScaling) - 80), 30);
        if (Screens.All.Any(s => s.WorkingArea.Intersects(top))) return;
        if ((Screens.Primary ?? Screens.All.FirstOrDefault()) is not { } screen) return;
        var area = screen.WorkingArea;
        Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - (int)(ClientSize.Width * screen.Scaling)) / 2),
            area.Y + Math.Max(0, (area.Height - (int)(ClientSize.Height * screen.Scaling)) / 2));
    }

    /// <summary>Open the channel editor, or bring it forward if it is already open.</summary>
    private void OnChannelsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (_channels is not null)
        {
            _channels.Activate();
            return;
        }
        var vm = new ChannelsViewModel(main.Store, main.ChannelColumns);
        vm.Edited += main.MemoriesEdited;
        vm.GroupRenamed += main.GroupRenamed;
        main.Reloaded += vm.StoreReloaded;
        _channels = new ChannelsWindow { DataContext = vm };
        _channels.TuneRequested += main.Tune;
        _channels.Closed += (_, _) =>
        {
            main.Reloaded -= vm.StoreReloaded;
            main.ChannelColumns = vm.ColumnWidths();
            _channels = null;
        };
        _channels.Show(this);
    }
}
