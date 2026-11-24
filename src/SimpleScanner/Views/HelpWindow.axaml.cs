using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace SimpleScanner.Views;

/// <summary>The Help: its sections listed at the left, the one chosen at the right. The text is in Assets/help.md.</summary>
public partial class HelpWindow : Window
{
    private readonly IReadOnlyList<HelpSection> _sections;

    public HelpWindow()
    {
        InitializeComponent();
        _sections = Help.Load();
        VersionText.Text = AppInfo.Title;
        Sections.ItemsSource = _sections.Select(s => s.Title).ToList();
        Sections.SelectedIndex = 0;
    }

    /// <summary>Show the section with this title, if there is one.</summary>
    public void Show(string title)
    {
        for (int i = 0; i < _sections.Count; i++)
            if (_sections[i].Title == title) Sections.SelectedIndex = i;
    }

    private void OnSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Sections.SelectedIndex < 0 || Sections.SelectedIndex >= _sections.Count) return;
        var section = _sections[Sections.SelectedIndex];
        Body.Children.Clear();
        Body.Children.Add(new TextBlock { Text = section.Title, FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var block in section.Blocks) Body.Children.Add(Draw(block));
        Scroll.Offset = default;
    }

    private Control Draw(HelpBlock block)
    {
        switch (block.Kind)
        {
            case HelpKind.Heading:
                return new TextBlock
                {
                    Text = block.Text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                };
            case HelpKind.Bullet:
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*") };
                row.Children.Add(new TextBlock { Text = "•", FontSize = 13, LineHeight = 20, Foreground = Brush("TextMuted") });
                var text = Text(block);
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                return row;
            }
            case HelpKind.Note:
                return new Border
                {
                    Background = Brush("ControlBg"), BorderBrush = Brush("Accent"), BorderThickness = new Thickness(3, 0, 0, 0),
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 10), Margin = new Thickness(0, 4),
                    Child = Text(block),
                };
            default:
                return Text(block);
        }
    }

    // Text that can be selected and copied, with its bold runs.
    private static SelectableTextBlock Text(HelpBlock block)
    {
        var text = new SelectableTextBlock
        {
            FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var (run, bold) in block.Runs())
            text.Inlines!.Add(new Run(run) { FontWeight = bold ? FontWeight.Bold : FontWeight.Normal });
        return text;
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;
}
