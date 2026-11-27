// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Views;

/// <summary>Who made it, the licence and the credits, and a link that asks GitHub for a newer release.</summary>
public partial class AboutWindow : Window
{
    public const string ProjectPage = "https://github.com/kingsqueak/SimpleScanner";

    /// <summary>What it says, heading by heading.</summary>
    public static readonly (string Head, string Text)[] Sections =
    [
        ("About", "A scanner for SDRplay RSP receivers: stored channels in groups, RadioReference, Multi Monitor and digital voice. By Chris, KC2RGW."),
        ("Licence", "Copyright © 2026 KC2RGW Chris. SimpleScanner is free software under the GNU General Public License, version 3 or (at your option) any later version: GPL-3.0-or-later. It comes with no warranty."),
        ("With thanks to", "miniaudio (audio output), RadioFoundry and DSD (digital voice receivers), and mbelib (speech decoders), each under its own licence. The window is built with Avalonia and SkiaSharp, with the Inter typeface. Their notices are in THIRD-PARTY-NOTICES.txt, which comes with the app."),
        ("Patents", "The AMBE and AMBE+2 speech decoders may be covered by patents in some countries: see the README."),
        ("Names", "SDRplay and RSP are SDRplay's names; RadioReference is RadioReference.com's. SimpleScanner is not connected with either."),
    ];

    private readonly MainViewModel? _main;
    private bool _checking;

    public AboutWindow() : this(null) { }

    /// <param name="main">Told of a newer release found here, so the note in the main window shows it too.</param>
    public AboutWindow(MainViewModel? main)
    {
        _main = main;
        InitializeComponent();
        VersionText.Text = AppInfo.Version.Length > 0 ? $"Version {AppInfo.Version}" : "";
        foreach (var (head, text) in Sections)
        {
            Body.Children.Add(new TextBlock { Text = head, FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            Body.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
        }
        Body.Children.Add(Link("github.com/kingsqueak/SimpleScanner", ProjectPage, 12));
        Body.Children.Add(Link("Bug reports and suggestions: Issues", ProjectPage + "/issues", 4));
        CheckButton.Click += async (_, _) => await CheckAsync();
        CloseButton.Click += (_, _) => Close();
    }

    private static HyperlinkButton Link(string text, string url, double top) => new()
    {
        Content = text, NavigateUri = new Uri(url), Padding = new Thickness(0), FontSize = 13, Margin = new Thickness(0, top, 0, 0),
    };

    /// <summary>Ask GitHub for the latest release and say what it found under the version.</summary>
    public async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        CheckButton.IsEnabled = false;
        ReleaseLink.IsVisible = false;
        CheckResult.Text = "Checking…";
        CheckResult.Foreground = (IBrush?)this.FindResource(ActualThemeVariant, "TextMuted") ?? Brushes.Gray;
        var latest = await UpdateChecker.FetchLatestAsync();
        if (latest is not null) _main?.ShowUpdate(latest);
        ShowResult(latest, AppInfo.Version);
        CheckButton.IsEnabled = true;
        _checking = false;
    }

    /// <summary>What a check found: the line beside the link, and the release's page when there is a newer one.</summary>
    public void ShowResult(UpdateChecker.Release? latest, string current)
    {
        var (text, link) = UpdateChecker.Describe(latest, current);
        CheckResult.Text = text;
        CheckResult.Foreground = (IBrush?)this.FindResource(ActualThemeVariant, link is not null ? "WarnText" : "TextMuted") ?? Brushes.Gray;
        ReleaseLink.IsVisible = link is not null;
        if (link is null) return;
        ReleaseLink.Content = link.Value.Text;
        ReleaseLink.NavigateUri = new Uri(link.Value.Url);
    }
}
