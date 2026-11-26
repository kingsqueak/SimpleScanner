using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace SimpleScanner.Views;

/// <summary>The readout: the contents of the scanner's display, used by the full window and the small one.</summary>
public partial class Display : UserControl
{
    public Display() => InitializeComponent();

    /// <summary>A Multi Monitor row's particulars, after whose channel it is on the same line: set apart by a gap when both are there.</summary>
    public static readonly IMultiValueConverter AfterDetail = new FuncMultiValueConverter<string?, string>(parts =>
    {
        var p = parts.ToArray();
        string detail = p.Length > 0 ? p[0] ?? "" : "", info = p.Length > 1 ? p[1] ?? "" : "";
        return detail.Length > 0 && info.Length > 0 ? "   " + info : info;
    });

    /// <summary>The texts that are there, one to a line, for a tip; none when all are empty.</summary>
    public static readonly IMultiValueConverter Lines = new FuncMultiValueConverter<string?, string?>(parts =>
    {
        string all = string.Join("\n", parts.Where(s => !string.IsNullOrEmpty(s)));
        return all.Length > 0 ? all : null;
    });
}
