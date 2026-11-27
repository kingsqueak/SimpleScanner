// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    /// <summary>The RadioReference browser was asked for: the main window, which holds the settings, opens it.</summary>
    public event Action? OnlineRequested;

    private void OnOnlineClick(object? sender, RoutedEventArgs e)
    {
        Vm?.Flush();
        OnlineRequested?.Invoke();
    }

    public ChannelsWindow()
    {
        InitializeComponent();
        // Edits are written a moment after they are made, not at every keystroke.
        _save = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Vm?.Flush());
        _save.Start();
        Activated += (_, _) => Vm?.ReloadIfChanged();
        DataContextChanged += (_, _) =>
        {
            if (Vm is { } vm) vm.SelectRequested += Select;
        };
        Closed += (_, _) =>
        {
            _save.Stop();
            Vm?.Flush();
        };
    }

    private ChannelsViewModel? Vm => DataContext as ChannelsViewModel;

    /// <summary>Select these rows in the list, and no others.</summary>
    private void Select(IReadOnlyList<ChannelRow> rows)
    {
        if (Vm is not { } vm) return;
        var wanted = rows.ToHashSet();
        List.Selection.BeginBatchUpdate();
        List.Selection.Clear();
        for (int i = 0; i < vm.Rows.Count; i++)
            if (wanted.Contains(vm.Rows[i])) List.Selection.Select(i);
        List.Selection.EndBatchUpdate();
    }

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

    // ---- column widths --------------------------------------------------------------------------

    private const int DescriptionColumn = 4;
    private const double NarrowestDescription = 80;

    /// <summary>A heading's handle is being dragged. The column grows or shrinks and the description
    /// column, which takes the room that is left, gives or takes the difference. The handles of the
    /// columns right of the description are on their left edge, so there the drag counts the other way.</summary>
    private void OnHeadDrag(object? sender, VectorEventArgs e)
    {
        if (sender is not Thumb { DataContext: ColumnHead head } thumb) return;
        double change = thumb.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Left ? -e.Vector.X : e.Vector.X;
        double now = head.Width.Value;
        double room = Math.Max(0, HeadGrid.ColumnDefinitions[DescriptionColumn].ActualWidth - NarrowestDescription);
        head.Width = new GridLength(Math.Round(Math.Clamp(now + change, Math.Min(now, ColumnHead.Narrowest), now + room)));
    }

    /// <summary>A double-click on a handle puts the column back to its normal width.</summary>
    private void OnHeadReset(object? sender, TappedEventArgs e)
    {
        if (sender is Thumb { DataContext: ColumnHead head }) head.Width = new GridLength(head.NormalWidth);
        e.Handled = true;
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
