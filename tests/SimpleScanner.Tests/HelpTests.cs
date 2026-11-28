// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner.Tests;

/// <summary>The Help text: how it is read, and that the text in the program has the parts it must have.</summary>
public class HelpTests
{
    [Fact]
    public void Sections_headings_bullets_notes_and_paragraphs_are_told_apart()
    {
        var help = Help.Parse("ignored before the first section\n# One\nA paragraph\nover two lines.\n\n## Inside\n- a **bold** bullet\n> a note\n# Two\nlast");
        Assert.Equal(["One", "Two"], help.Select(s => s.Title));
        Assert.Equal(
            [new(HelpKind.Paragraph, "A paragraph over two lines."), new(HelpKind.Heading, "Inside"),
             new(HelpKind.Bullet, "a **bold** bullet"), new(HelpKind.Note, "a note")],
            help[0].Blocks);
        Assert.Equal([("a ", false), ("bold", true), (" bullet", false)], help[0].Blocks[2].Runs());
        Assert.Equal([new HelpBlock(HelpKind.Paragraph, "last")], help[1].Blocks);
    }

    [Fact]
    public void The_help_in_the_program_has_its_sections_and_names_every_radio()
    {
        var help = Help.Load();
        string[] wanted = ["Getting started", "Trunk mode", "RadioReference", "Several radios", "Hardware", "About"];
        Assert.Equal("Getting started", help[0].Title);
        Assert.All(wanted, title => Assert.Contains(help, s => s.Title == title));
        Assert.All(help, s => Assert.NotEmpty(s.Blocks));
        // Every ** has its partner, so no run of text is bold by mistake.
        Assert.All(help.SelectMany(s => s.Blocks), b => Assert.True(b.Text.Split("**").Length % 2 == 1, b.Text));

        string Text(string title) => string.Join('\n', help.First(s => s.Title == title).Blocks.Select(b => b.Text));
        // Every model the radio's software is asked about (SdrPlaySource.ModelName).
        Assert.All(new[] { "RSP1", "RSP1A", "RSP1B", "RSP2", "RSPduo", "RSPdx", "RSPdx-R2" }, model => Assert.Contains(model, Text("Hardware")));
        Assert.Contains("CSV", Text("RadioReference"));
        Assert.Contains("application key", Text("RadioReference"));
        Assert.Contains("USB", Text("Several radios"));
        // What trunk mode follows, what it does not, and every outcome of a call the Trunk tab shows.
        Assert.All(new[] { "Phase 2", "TDMA", "simulcast", "Hold", "EDACS", "unit to unit" }, w => Assert.Contains(w, Text("Trunk mode")));
        Assert.All(new[] { "heard", "busy", "not ticked", "encrypted", "lost", "no voice codec" }, w => Assert.Contains($"**{w}**", Text("Trunk mode")));
    }

    [Fact]
    public void The_version_is_three_numbers()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
        Assert.Equal($"SimpleScanner {AppInfo.Version}", AppInfo.Title);
    }
}
