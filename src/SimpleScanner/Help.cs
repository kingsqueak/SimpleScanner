using System.Reflection;

namespace SimpleScanner;

/// <summary>The program's name and version, as shown in the menu bar and in Help.</summary>
public static class AppInfo
{
    public const string Name = "SimpleScanner";

    /// <summary>The version from the project file, as three numbers: 0.1.1.</summary>
    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>"SimpleScanner 0.1.1".</summary>
    public static string Title { get; } = Version.Length > 0 ? $"{Name} {Version}" : Name;
}

/// <summary>What a piece of the Help text is.</summary>
public enum HelpKind { Heading, Paragraph, Bullet, Note }

/// <summary>One piece of a section. Its text may hold **bold** runs.</summary>
public sealed record HelpBlock(HelpKind Kind, string Text)
{
    /// <summary>The text cut at its ** marks: the runs, and whether each is bold.</summary>
    public IEnumerable<(string Text, bool Bold)> Runs()
    {
        var parts = Text.Split("**");
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) yield return (parts[i], i % 2 == 1);
    }
}

/// <summary>A section of the Help: one line in the list at the left of the Help window.</summary>
public sealed record HelpSection(string Title, IReadOnlyList<HelpBlock> Blocks);

/// <summary>
/// The Help text. It is written in Assets/help.md in a small part of Markdown: "# " starts a
/// section, "## " a heading inside it, "- " a bullet, "> " a note, and anything else is a
/// paragraph, ended by an empty line. **Two stars** each side make bold.
/// </summary>
public static class Help
{
    /// <summary>The Help that comes with the program.</summary>
    public static IReadOnlyList<HelpSection> Load()
    {
        using var stream = typeof(Help).Assembly.GetManifestResourceStream("SimpleScanner.help.md")
            ?? throw new InvalidOperationException("The Help text is missing from the program.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<HelpSection> Parse(string text)
    {
        var sections = new List<HelpSection>();
        List<HelpBlock>? blocks = null;
        var paragraph = new List<string>();
        void EndParagraph()
        {
            if (paragraph.Count > 0) blocks?.Add(new HelpBlock(HelpKind.Paragraph, string.Join(' ', paragraph)));
            paragraph.Clear();
        }
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("# "))
            {
                EndParagraph();
                blocks = [];
                sections.Add(new HelpSection(line[2..].Trim(), blocks));
            }
            else if (line.Length == 0) EndParagraph();
            else if (line.StartsWith("## ")) Add(HelpKind.Heading, 3);
            else if (line.StartsWith("- ")) Add(HelpKind.Bullet, 2);
            else if (line.StartsWith("> ")) Add(HelpKind.Note, 2);
            else paragraph.Add(line);

            void Add(HelpKind kind, int skip)
            {
                EndParagraph();
                blocks?.Add(new HelpBlock(kind, line[skip..].Trim()));
            }
        }
        EndParagraph();
        return sections;
    }
}
