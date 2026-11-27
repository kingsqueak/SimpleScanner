using Avalonia.Data.Converters;

namespace SimpleScanner.Views;

/// <summary>Shorter words for a choice where it sits in a row of buttons.</summary>
public static class Short
{
    /// <summary>A filter's name without " channel": "12.5 kHz channel" is "12.5 kHz".</summary>
    public static readonly IValueConverter Width = new FuncValueConverter<string?, string?>(s => s?.Replace(" channel", ""));
}
