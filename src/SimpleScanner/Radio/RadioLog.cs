namespace SimpleScanner.Radio;

/// <summary>
/// A line about a radio's troubles (a stall, a replug, a restart) to the error output and to
/// ~/Library/Logs/SimpleScanner.log, which a user can send in. Written by the app and its helpers alike.
/// </summary>
public static class RadioLog
{
    private static readonly object Gate = new();

    /// <summary>For tests: write here, not to the user's log.</summary>
    internal static string? FileOverride { get; set; }

    public static void Write(string what)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}";
        lock (Gate)
        {
            Console.Error.WriteLine(line);
            try
            {
                string file = FileOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "SimpleScanner.log");
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
