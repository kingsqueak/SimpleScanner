using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Views;

/// <summary>The channel editor's window: the list's selection, the file chooser and the saving are done here.</summary>
public partial class ChannelsWindow : Window
{
    private readonly DispatcherTimer _save;

    /// <summary>A channel was double-clicked: the main window tunes to it.</summary>
    public event Action<MemoryChannel>? TuneRequested;

    public ChannelsWindow()
    {
        InitializeComponent();
        // Edits are written a moment after they are made, not at every keystroke.
        _save = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Vm?.Flush());
        _save.Start();
        Activated += (_, _) => Vm?.ReloadIfChanged();
        Closed += (_, _) =>
        {
            _save.Stop();
            Vm?.Flush();
        };
    }

    private ChannelsViewModel? Vm => DataContext as ChannelsViewModel;

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Vm?.SetSelection(List.SelectedItems?.OfType<ChannelRow>().ToList() ?? []);

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm?.Selected is { } row)
        {
            Vm.Flush();
            TuneRequested?.Invoke(row.Channel);
        }
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "RadioReference frequency downloads",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("CSV files") { Patterns = ["*.csv"] }, FilePickerFileTypes.All],
            });
            foreach (var file in files)
            {
                await using var stream = await file.OpenReadAsync();
                using var reader = new StreamReader(stream);
                vm.OfferImport(file.Name, await reader.ReadToEndAsync());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            vm.Status = ex.Message;
        }
    }
}
