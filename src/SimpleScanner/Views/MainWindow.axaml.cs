using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimpleScanner.RadioReference;
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

    // The full window's and the small window's least and usual sizes.
    private const double FullMinWidth = 700, FullMinHeight = 634, FullWidth = 1130, FullHeight = 664;
    private const double MiniMinWidth = 300, MiniMinHeight = 170, MiniWidth = 520, MiniHeight = 262;

    /// <summary>Put the window where it was when it was last closed, at the size it had, full or small as it was.</summary>
    public void RestorePlace()
    {
        if (DataContext is not MainViewModel main) return;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsMini)) ChangeView(main);
        };
        SizeFor(main);
        if ((main.IsMini ? main.MiniWindowPlace : main.WindowPlace) is not { } p) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(p.X, p.Y);
    }

    /// <summary>Give the window the limits and the size of the view it is showing: the size it last had, or the usual one.</summary>
    private void SizeFor(MainViewModel main)
    {
        bool mini = main.IsMini;
        var p = mini ? main.MiniWindowPlace : main.WindowPlace;
        double minW = mini ? MiniMinWidth : FullMinWidth, minH = mini ? MiniMinHeight : FullMinHeight;
        // Lower the limits before the size and raise them after, so neither clamps the other on the way.
        MinWidth = Math.Min(MinWidth, minW);
        MinHeight = Math.Min(MinHeight, minH);
        Width = Math.Max(p?.Width ?? (mini ? MiniWidth : FullWidth), minW);
        Height = Math.Max(p?.Height ?? (mini ? MiniHeight : FullHeight), minH);
        MinWidth = minW;
        MinHeight = minH;
    }

    /// <summary>The view has just changed: remember the size the old one had, and take the new one's. The top left corner stays put.</summary>
    private void ChangeView(MainViewModel main)
    {
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        else Keep(main, !main.IsMini);
        SizeFor(main);
    }

    /// <summary>Remember where the window is. A maximised or minimised window keeps the place it had before.</summary>
    public void KeepPlace()
    {
        if (DataContext is MainViewModel main && WindowState == WindowState.Normal) Keep(main, main.IsMini);
    }

    private void Keep(MainViewModel main, bool mini)
    {
        if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
        var place = new Settings.Place(Position.X, Position.Y, ClientSize.Width, ClientSize.Height);
        if (mini) main.MiniWindowPlace = place;
        else main.WindowPlace = place;
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

    private int _tab;

    /// <summary>The Memory Mgr tab is a button in the tab strip: it opens the channel editor and the tab that was showing comes back.</summary>
    // Operate's right-hand column: the user groups have up to half of it, so the active frequencies under them stay in view.
    private void OnGroupsColumnSized(object? sender, SizeChangedEventArgs e) =>
        GroupsScroll.MaxHeight = Math.Max(60, e.NewSize.Height * 0.5 - 20);

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Lists and choosers inside the tabs send this event up too.
        if (!ReferenceEquals(e.Source, Tabs)) return;
        if (!ReferenceEquals(Tabs.SelectedItem, MemoryTab))
        {
            _tab = Tabs.SelectedIndex;
            // Settings lists the radios: look for them as it is shown, so one just plugged in is there.
            if (Tabs.SelectedItem is TabItem { Header: "Settings" } && DataContext is MainViewModel main) main.RefreshRadios();
            return;
        }
        Tabs.SelectedIndex = _tab;
        // After the click is over, so the main window does not take the focus back from the editor.
        Avalonia.Threading.Dispatcher.UIThread.Post(OpenChannels);
    }

    /// <summary>Open the channel editor, or bring it forward if it is already open.</summary>
    private void OpenChannels()
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
        _channels.OnlineRequested += () => OpenRadioReference(main, vm);
        _channels.Closed += (_, _) =>
        {
            main.Reloaded -= vm.StoreReloaded;
            main.ChannelColumns = vm.ColumnWidths();
            _online?.Close();
            _channels = null;
        };
        _channels.Show(this);
    }

    private RadioReferenceWindow? _online;

    /// <summary>Open the RadioReference browser over the channel editor, or bring it forward.</summary>
    private void OpenRadioReference(MainViewModel main, ChannelsViewModel channels)
    {
        if (_online is not null)
        {
            _online.Activate();
            return;
        }
        var cache = new RrCache();
        var client = new RrClient(cache);
        var vm = new RadioReferenceViewModel(main.Store, client, main.RadioReferencePlace, cache: cache);
        vm.PlaceChanged += main.SaveSettings;
        vm.Loaded += () =>
        {
            channels.ReloadIfChanged();
            main.MemoriesEdited();
        };
        _online = new RadioReferenceWindow { DataContext = vm };
        _online.Closed += (_, _) =>
        {
            main.SaveSettings();
            client.Dispose();
            cache.Dispose();
            _online = null;
        };
        _online.Show((Window?)_channels ?? this);
    }
}
