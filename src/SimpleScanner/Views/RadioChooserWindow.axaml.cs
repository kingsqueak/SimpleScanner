using Avalonia.Controls;
using Avalonia.Interactivity;
using SimpleScanner.Radio;

namespace SimpleScanner.Views;

/// <summary>Asked at start when more than one radio is plugged in: which one to use. Closing the
/// window without choosing takes the one that was selected when it opened.</summary>
public partial class RadioChooserWindow : Window
{
    public RadioChooserWindow() => InitializeComponent();

    public RadioChooserWindow(IReadOnlyList<RadioInfo> radios, string? lastSerial) : this()
    {
        Radios.ItemsSource = radios;
        Radios.SelectedItem = Pick(radios, lastSerial);
        Opened += (_, _) => Radios.Focus();
    }

    /// <summary>The radio chosen, also when the window was closed by its close button.</summary>
    public RadioInfo? Chosen => Radios.SelectedItem as RadioInfo;

    /// <summary>The radio to offer first: the one used last time if it is there, or else the first in the list.</summary>
    public static RadioInfo? Pick(IReadOnlyList<RadioInfo> radios, string? lastSerial) =>
        radios.FirstOrDefault(r => r.Serial == lastSerial) ?? radios.FirstOrDefault();

    private void OnUse(object? sender, RoutedEventArgs e) => Close();
}
