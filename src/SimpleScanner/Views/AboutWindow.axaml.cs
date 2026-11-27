// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Views;

/// <summary>What SimpleScanner is and who made it, the licence and the credits, and a link that asks GitHub for a newer release.</summary>
public partial class AboutWindow : Window
{
    private readonly MainViewModel? _main;
    private bool _checking;

    public AboutWindow() : this(null) { }

    /// <param name="main">Told of a newer release found here, so the note in the main window shows it too.</param>
    public AboutWindow(MainViewModel? main)
    {
        _main = main;
        InitializeComponent();
        VersionText.Text = AppInfo.Version.Length > 0 ? $"Version {AppInfo.Version}" : "";
        CheckButton.Click += async (_, _) => await CheckAsync();
        CloseButton.Click += (_, _) => Close();
    }

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
