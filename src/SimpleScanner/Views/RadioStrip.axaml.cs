using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SimpleScanner.Controls;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Views;

/// <summary>One radio's display as a strip, for when several radios run at once. A click anywhere in it selects that radio.</summary>
public partial class RadioStrip : UserControl
{
    public RadioStrip()
    {
        InitializeComponent();
        // On the way down, so the radio is selected before a button in the strip does its work.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (DataContext is not RadioViewModel radio || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main) return;
            bool selecting = !radio.IsSelected;
            main.Select(radio);
            // The click that selects a radio stops here when it lands on the frequency: it is not also a step of the
            // digit under it. (A click low on a leading blank took 100 MHz off, which came out as 100 kHz and, on
            // 2026-11-26, as a radio that would not start after a jam.) The buttons still work at the first click.
            if (selecting && (e.Source as Visual)?.FindAncestorOfType<FrequencyReadout>(includeSelf: true) is not null) e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
}
