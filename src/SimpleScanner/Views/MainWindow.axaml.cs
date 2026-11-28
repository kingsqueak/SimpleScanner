// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
        Closed += (_, _) => _closed = true;
        Opened += (_, _) => BringOntoAScreen();
        BuildMenu();
    }

    // The Help menu in the menu bar. On macOS About is in the application's own menu (see App), elsewhere here.
    private void BuildMenu()
    {
        var help = new NativeMenuItem($"{AppInfo.Name} Help");
        help.Click += (_, _) => OpenHelp();
        var check = new NativeMenuItem("Check for Updates…");
        check.Click += (_, _) => OpenAbout(check: true);
        var items = new NativeMenu { help, check };
        if (!OperatingSystem.IsMacOS())
        {
            var about = new NativeMenuItem($"About {AppInfo.Name}");
            about.Click += (_, _) => OpenAbout();
            items.Add(new NativeMenuItemSeparator());
            items.Add(about);
        }
        NativeMenu.SetMenu(this, new NativeMenu { new NativeMenuItem("Help") { Menu = items } });
    }

    private AboutWindow? _about;

    /// <summary>Open About, or bring it forward if it is already open; with check, ask GitHub for a newer release as well.</summary>
    public void OpenAbout(bool check = false)
    {
        if (_about is null)
        {
            _about = new AboutWindow(DataContext as MainViewModel);
            _about.Closed += (_, _) => _about = null;
            _about.Show(this);
        }
        else _about.Activate();
        if (check) _ = _about.CheckAsync();
    }

    // The full window's and the small window's least and usual sizes.
    // On Windows and Linux the Help menu is a bar at the top of the full window, so it is that much taller.
    private static readonly double MenuBar = OperatingSystem.IsMacOS() ? 0 : 26;
    private static readonly double FullMinWidth = 820, FullMinHeight = 740 + MenuBar, FullWidth = 1130, FullHeight = 760 + MenuBar;
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

    /// <summary>
    /// The view has just changed: remember where the old one was and how big, and take the new one's place and size.
    /// Each view has a place of its own; a view with none yet opens where the top left corner of the other was.
    /// </summary>
    private void ChangeView(MainViewModel main)
    {
        var from = Position;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        else Keep(main, !main.IsMini);
        SizeFor(main);
        var saved = main.IsMini ? main.MiniWindowPlace : main.WindowPlace;
        var at = saved is { } p ? new PixelPoint(p.X, p.Y) : from;
        MoveOnScreen(at);
        // macOS resizes about the bottom left corner, and may do so after this: put it in its place again then.
        Dispatcher.UIThread.Post(() => MoveOnScreen(at), DispatcherPriority.Background);
    }

    /// <summary>
    /// Remember where the window is. A maximised or minimised window keeps the place it had before, and so does one
    /// already closed: closed with its close button, the app's shutdown asks again, and its place then reads as 0,0.
    /// </summary>
    public void KeepPlace()
    {
        if (_closed || !IsVisible) return;
        if (DataContext is MainViewModel main && WindowState == WindowState.Normal) Keep(main, main.IsMini);
    }

    private bool _closed;

    private void Keep(MainViewModel main, bool mini)
    {
        if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
        var place = new Settings.Place(Position.X, Position.Y, ClientSize.Width, ClientSize.Height);
        if (mini) main.MiniWindowPlace = place;
        else main.WindowPlace = place;
    }

    /// <summary>At start, the place it was given: kept wholly on a screen (see <see cref="MoveOnScreen"/>).</summary>
    private void BringOntoAScreen()
    {
        if (WindowStartupLocation == WindowStartupLocation.Manual) MoveOnScreen(Position);
    }

    /// <summary>Put the window's top left corner at this point, moved as little as needed for all of it to be on a screen.</summary>
    private void MoveOnScreen(PixelPoint at)
    {
        var screens = Screens.All;
        if (screens.Count == 0) return;
        // The size it is about to have: the size asked for, with its title bar and edges, in screen pixels.
        double frameW = (FrameSize?.Width ?? ClientSize.Width) - ClientSize.Width;
        double frameH = (FrameSize?.Height ?? ClientSize.Height) - ClientSize.Height;
        var current = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? screens[0];
        var place = WindowPlacement.Fit(at,
            (w: Math.Max(0, frameW) + (double.IsNaN(Width) ? ClientSize.Width : Width),
             h: Math.Max(0, frameH) + (double.IsNaN(Height) ? ClientSize.Height : Height)),
            screens.Select(s => (s.WorkingArea, s.Scaling)).ToList(),
            screens.ToList().IndexOf(current));
        if (place != Position) Position = place;
    }

    // The deck: its three cards in one row when there is room for them; otherwise Scan and Audio side by side
    // over Tune, which then has the width for its controls in one line.
    private bool? _wideDeck;

    private void OnDeckSized(object? sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= 1000;
        if (_wideDeck == wide) return;
        _wideDeck = wide;
        Deck.ColumnDefinitions = new ColumnDefinitions(wide ? "*,1.4*,*" : "*,*");
        Deck.RowDefinitions = new RowDefinitions(wide ? "Auto" : "Auto,Auto");
        Place(ScanCard, 0, 0, 1);
        Place(TuneCard, wide ? 0 : 1, wide ? 1 : 0, wide ? 1 : 2);
        Place(AudioCard, 0, wide ? 2 : 1, 1);
    }

    private static void Place(Control card, int row, int column, int span)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        Grid.SetColumnSpan(card, span);
    }

    // The deck's Trunk: starts or stops trunk mode for the radio selected; with no site chosen for it yet (or no system
    // stored), it opens Trunk, where they are chosen.
    private void OnTrunk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        var radio = main.Radio;
        if (!radio.IsTrunking && radio.Trunk.SelectedSite is null)
        {
            Tabs.SelectedItem = TrunkTab;
            return;
        }
        radio.ToggleTrunkCommand.Execute(null);
    }

    // The deck's line about the groups scanned opens Groups.
    private void OnShowGroups(object? sender, RoutedEventArgs e) => Tabs.SelectedItem = GroupsTab;

    // Store, on the deck and on Search: Channels, with the name box ready to type in.
    private void OnStore(object? sender, RoutedEventArgs e)
    {
        Tabs.SelectedItem = ChannelsTab;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => StoreName.Focus(), Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnOpenChannels(object? sender, RoutedEventArgs e) =>
        // After the click is over, so the main window does not take the focus back from the editor.
        Avalonia.Threading.Dispatcher.UIThread.Post(OpenChannels);

    private void OnOpenHelp(object? sender, RoutedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(() => OpenHelp());

    // The correction's − and + buttons: a tenth of a ppm at a time.
    private void OnPpmDown(object? sender, RoutedEventArgs e) => StepPpm(-0.1m);
    private void OnPpmUp(object? sender, RoutedEventArgs e) => StepPpm(0.1m);

    private void StepPpm(decimal by)
    {
        if (DataContext is MainViewModel main) main.Radio.Ppm = Math.Clamp(Math.Round((main.Radio.Ppm ?? 0) + by, 1), -100, 100);
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Lists and choosers inside the tabs send this event up too.
        if (!ReferenceEquals(e.Source, Tabs)) return;
        // Radio lists the radios: look for them as it is shown, so one just plugged in is there.
        if (ReferenceEquals(Tabs.SelectedItem, RadioTab) && DataContext is MainViewModel main) main.RefreshRadios();
    }

    private HelpWindow? _help;

    /// <summary>Open the Help, or bring it forward if it is already open; at this section, when one is named.</summary>
    public void OpenHelp(string? section = null)
    {
        if (_help is null)
        {
            _help = new HelpWindow();
            _help.Closed += (_, _) => _help = null;
            _help.Show(this);
        }
        else _help.Activate();
        if (section is not null) _help.Show(section);
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
        var vm = new RadioReferenceViewModel(main.Store, client, main.RadioReferencePlace, cache: cache, trunks: main.Trunks);
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
