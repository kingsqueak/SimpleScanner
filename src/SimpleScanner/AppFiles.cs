namespace SimpleScanner;

/// <summary>Where the program keeps what it stores, beside the settings.</summary>
public static class AppFiles
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleScanner");

    /// <summary>
    /// The one database (SQLite): the stored channels, and what has been fetched from RadioReference.
    /// It is there whether or not a RadioReference account is used.
    /// </summary>
    public static string Database { get; } = Path.Combine(Folder, "simplescanner.db");

    /// <summary>The stored channels as they were kept before the database: read once, into it.</summary>
    public static string OldChannels { get; } = Path.Combine(Folder, "memories.json");
    /// <summary>What <see cref="OldChannels"/> is renamed to once its channels are in the database.</summary>
    public static string OldChannelsKept { get; } = Path.Combine(Folder, "memories.json.moved-into-database");
    private static string OldDatabase { get; } = Path.Combine(Folder, "radioreference.db");

    /// <summary>
    /// Bring what earlier versions kept into the one database: the RadioReference answers (the file
    /// is renamed) and the channels of memories.json (read in, and the file put aside under another name).
    /// </summary>
    /// <returns>The stored channels.</returns>
    public static MemoryStore OpenStore()
    {
        try
        {
            if (!File.Exists(Database) && File.Exists(OldDatabase)) File.Move(OldDatabase, Database);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var store = MemoryStore.Load();
        try
        {
            if (File.Exists(OldChannels))
            {
                store.AddFromJson(OldChannels);
                // Put aside only once the channels are safely in the database.
                if (store.Save()) File.Move(OldChannels, OldChannelsKept, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return store;
    }
}
