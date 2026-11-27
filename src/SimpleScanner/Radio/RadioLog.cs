// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner.Radio;

/// <summary>
/// A line about a radio's troubles (a stall, a replug, a restart) to the error output and to
/// SimpleScanner.log, which a user can send in: in ~/Library/Logs on macOS, in the data folder (<see cref="AppFiles.Folder"/>)
/// elsewhere. Written by the app and its helpers alike.
/// </summary>
public static class RadioLog
{
    private static readonly object Gate = new();

    /// <summary>For tests: write here, not to the user's log.</summary>
    internal static string? FileOverride { get; set; }

    /// <summary>Where the log is: ~/Library/Logs on macOS, the data folder elsewhere.</summary>
    public static string DefaultFile => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "SimpleScanner.log")
        : Path.Combine(AppFiles.Folder, "SimpleScanner.log");

    public static void Write(string what)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}";
        lock (Gate)
        {
            Console.Error.WriteLine(line);
            try
            {
                string file = FileOverride ?? DefaultFile;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
