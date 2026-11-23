using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace SimpleScanner.Controls;

/// <summary>
/// The big frequency display, in MHz to 10 Hz. Each digit tunes by its own place: roll the
/// mouse wheel over it, or click its upper half to go up and its lower half to go down.
/// </summary>
public sealed class FrequencyReadout : Control
{
    private const int Cells = 10;        // "1234.56789"
    private const int DotCell = 4;

    public static readonly StyledProperty<double> FrequencyHzProperty =
        AvaloniaProperty.Register<FrequencyReadout, double>(nameof(FrequencyHz), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<FrequencyReadout, IBrush?>(nameof(Foreground), Brushes.White);
    public static readonly StyledProperty<IBrush?> DimForegroundProperty =
        AvaloniaProperty.Register<FrequencyReadout, IBrush?>(nameof(DimForeground), Brushes.Gray);
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<FrequencyReadout, double>(nameof(FontSize), 56);

    private static readonly Typeface Face = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"), FontStyle.Normal, FontWeight.Medium);

    static FrequencyReadout()
    {
        AffectsRender<FrequencyReadout>(FrequencyHzProperty, ForegroundProperty, DimForegroundProperty, FontSizeProperty);
        AffectsMeasure<FrequencyReadout>(FontSizeProperty);
    }

    public double FrequencyHz { get => GetValue(FrequencyHzProperty); set => SetValue(FrequencyHzProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>For leading blanks' successors that matter less: the 100 Hz and 10 Hz digits.</summary>
    public IBrush? DimForeground { get => GetValue(DimForegroundProperty); set => SetValue(DimForegroundProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    private FormattedText Glyph(string s, IBrush? brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, FontSize, brush);

    private Size CellSize()
    {
        var zero = Glyph("0", null);
        return new Size(Math.Ceiling(zero.WidthIncludingTrailingWhitespace), Math.Ceiling(zero.Height));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cell = CellSize();
        return new Size(cell.Width * Cells, cell.Height);
    }

    public override void Render(DrawingContext context)
    {
        // A transparent fill so the whole control, not just the glyph ink, takes the mouse.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var cell = CellSize();
        string text = (FrequencyHz / 1e6).ToString("F5", CultureInfo.InvariantCulture).PadLeft(Cells);
        if (text.Length > Cells) text = text[^Cells..];
        for (int c = 0; c < Cells; c++)
        {
            if (text[c] == ' ') continue;
            var glyph = Glyph(text[c].ToString(), c >= Cells - 2 ? DimForeground : Foreground);
            context.DrawText(glyph, new Point(c * cell.Width + (cell.Width - glyph.WidthIncludingTrailingWhitespace) / 2, 0));
        }
    }

    /// <summary>The value of one count of the digit under x, in Hz; 0 for the decimal point.</summary>
    private double PlaceAt(double x)
    {
        int c = (int)(x / CellSize().Width);
        if (c < 0 || c >= Cells || c == DotCell) return 0;
        return c < DotCell ? 1e6 * Math.Pow(10, DotCell - 1 - c) : 1e6 / Math.Pow(10, c - DotCell);
    }

    private void Nudge(double x, int direction)
    {
        double place = PlaceAt(x);
        if (place == 0) return;
        FrequencyHz = Math.Round((FrequencyHz + direction * place) / 10) * 10;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        Nudge(e.GetPosition(this).X, e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        Nudge(p.X, p.Y < Bounds.Height / 2 ? 1 : -1);
        e.Handled = true;
    }
}
