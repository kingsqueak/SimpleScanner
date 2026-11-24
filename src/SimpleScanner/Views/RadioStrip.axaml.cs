using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Views;

/// <summary>One radio's display as a strip, for when several radios run at once. A click anywhere in it selects that radio.</summary>
public partial class RadioStrip : UserControl
{
    public RadioStrip()
    {
        InitializeComponent();
        // On the way down, so the radio is selected before a button in the strip does its work.
        AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (DataContext is RadioViewModel radio && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main) main.Select(radio);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
}
