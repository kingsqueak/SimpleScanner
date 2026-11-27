// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

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
    private const double FullMinWidth = 820, FullMinHeight = 740, FullWidth = 1130, FullHeight = 760;
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
