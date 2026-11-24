using Avalonia.Controls;
using Avalonia.Interactivity;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Views;

public partial class MainWindow : Window
{
    private ChannelsWindow? _channels;

    public MainWindow() => InitializeComponent();

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
        _channels = new ChannelsWindow { DataContext = vm };
        _channels.TuneRequested += main.Tune;
        _channels.Closed += (_, _) =>
        {
            main.ChannelColumns = vm.ColumnWidths();
            _channels = null;
        };
        _channels.Show(this);
    }
}
