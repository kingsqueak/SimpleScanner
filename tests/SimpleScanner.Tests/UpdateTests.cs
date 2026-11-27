// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Text.Json;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("0.1.8", "0.1.7", true)]
    [InlineData("0.1.10", "0.1.9", true)]
    [InlineData("v0.2", "0.1.7", true)]
    [InlineData("0.1.7", "0.1.7", false)]
    [InlineData("0.1.6", "0.1.7", false)]
    [InlineData("0.1.7-beta", "0.1.7", false)]
    [InlineData("1.0.0", "0.9.99", true)]
    public void Versions_are_compared_number_by_number(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public void GitHubs_answer_gives_the_version_and_its_page()
    {
        using var ok = JsonDocument.Parse("""{"tag_name":"v0.1.8","html_url":"https://github.com/kingsqueak/SimpleScanner/releases/tag/v0.1.8"}""");
        Assert.Equal(new UpdateChecker.Release("0.1.8", "https://github.com/kingsqueak/SimpleScanner/releases/tag/v0.1.8"), UpdateChecker.Parse(ok.RootElement));
        // A page that is not GitHub's is not opened: the Releases page is, in its place.
        using var odd = JsonDocument.Parse("""{"tag_name":"v0.1.8","html_url":"https://example.com/x"}""");
        Assert.Equal(UpdateChecker.ReleasesPage, UpdateChecker.Parse(odd.RootElement)!.Page);
        using var none = JsonDocument.Parse("""{"message":"Not Found"}""");
        Assert.Null(UpdateChecker.Parse(none.RootElement));
    }

    [Fact]
    public void About_says_what_the_check_found()
    {
        Assert.Contains("GitHub didn't answer", UpdateChecker.Describe(null, "0.1.7").Text);
        Assert.Equal(UpdateChecker.ReleasesPage, UpdateChecker.Describe(null, "0.1.7").Link!.Value.Url);
        Assert.Null(UpdateChecker.Describe(new("0.1.7", UpdateChecker.ReleasesPage), "0.1.7").Link);
        var newer = UpdateChecker.Describe(new("0.1.8", "https://github.com/kingsqueak/SimpleScanner/releases/tag/v0.1.8"), "0.1.7");
        Assert.Contains("0.1.8 is available", newer.Text);
        Assert.Equal("https://github.com/kingsqueak/SimpleScanner/releases/tag/v0.1.8", newer.Link!.Value.Url);
    }

    [Fact]
    public void The_note_shows_a_newer_release_and_its_cross_hides_it_until_the_next()
    {
        var dir = Directory.CreateTempSubdirectory("ss-update-");
        try
        {
            string path = Path.Combine(dir.FullName, "settings.json"), db = Path.Combine(dir.FullName, "s.db");
            using var rx = new Receiver(new FakeSource { Serial = "A" }, playAudio: false, sampleRate: 2.4e6);
            var main = new MainViewModel(Settings.Load(path), [rx], new MemoryStore(db));
            main.ShowUpdate(new("0.1.7", UpdateChecker.ReleasesPage), "0.1.7");
            Assert.Equal("", main.UpdateNote);
            main.ShowUpdate(new("0.1.8", "https://github.com/kingsqueak/SimpleScanner/releases/tag/v0.1.8"), "0.1.7");
            Assert.Equal("Version 0.1.8 is available", main.UpdateNote);
            Assert.EndsWith("/v0.1.8", main.UpdatePage.ToString());
            main.DismissUpdateCommand.Execute(null);
            Assert.Equal("", main.UpdateNote);
            Assert.Equal("0.1.8", Settings.Load(path).UpdateDismissedVersion);
            main.Shutdown();
            // Hidden at the next start too, until a later version comes out.
            using var rx2 = new Receiver(new FakeSource { Serial = "A" }, playAudio: false, sampleRate: 2.4e6);
            var next = new MainViewModel(Settings.Load(path), [rx2], new MemoryStore(db));
            next.ShowUpdate(new("0.1.8", UpdateChecker.ReleasesPage), "0.1.7");
            Assert.Equal("", next.UpdateNote);
            next.ShowUpdate(new("0.1.9", UpdateChecker.ReleasesPage), "0.1.7");
            Assert.Equal("Version 0.1.9 is available", next.UpdateNote);
            next.Shutdown();
        }
        finally { dir.Delete(true); }
    }
}
