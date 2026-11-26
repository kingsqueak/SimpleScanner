using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SimpleScanner.Controls;

/// <summary>A horizontal signal bar with a mark where the squelch opens.</summary>
public sealed class LevelBar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<LevelBar, double>(nameof(Value), -140);
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<LevelBar, double>(nameof(Minimum), -140);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<LevelBar, double>(nameof(Maximum), -20);
    public static readonly StyledProperty<double> MarkerProperty = AvaloniaProperty.Register<LevelBar, double>(nameof(Marker), -100);
    public static readonly StyledProperty<bool> ShowMarkerProperty = AvaloniaProperty.Register<LevelBar, bool>(nameof(ShowMarker), true);
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<LevelBar, IBrush?>(nameof(Fill), Brushes.Orange);
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<LevelBar, IBrush?>(nameof(Track), Brushes.DimGray);
    public static readonly StyledProperty<IBrush?> MarkerBrushProperty = AvaloniaProperty.Register<LevelBar, IBrush?>(nameof(MarkerBrush), Brushes.White);

    static LevelBar() => AffectsRender<LevelBar>(ValueProperty, MinimumProperty, MaximumProperty, MarkerProperty,
        ShowMarkerProperty, FillProperty, TrackProperty, MarkerBrushProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Marker { get => GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }
    public bool ShowMarker { get => GetValue(ShowMarkerProperty); set => SetValue(ShowMarkerProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public IBrush? MarkerBrush { get => GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value); }

    private double Fraction(double v) => Math.Clamp((v - Minimum) / Math.Max(1e-9, Maximum - Minimum), 0, 1);

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        // Not laid out yet (a row of the display just made): nothing to draw, and the marker's clamp would have no room.
        if (w < 2 || h <= 0) return;
        context.FillRectangle(Track ?? Brushes.Transparent, new Rect(0, 0, w, h), 3);
        double fill = w * Fraction(Value);
        if (fill > 0.5) context.FillRectangle(Fill ?? Brushes.Orange, new Rect(0, 0, fill, h), 3);
        if (ShowMarker)
        {
            double x = Math.Clamp(w * Fraction(Marker), 1, w - 1);
            context.FillRectangle(MarkerBrush ?? Brushes.White, new Rect(x - 1, -2, 2, h + 4));
        }
    }
}
