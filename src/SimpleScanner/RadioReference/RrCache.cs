using Microsoft.Data.Sqlite;

namespace SimpleScanner.RadioReference;

/// <summary>
/// What has been fetched from RadioReference, kept in a SQLite file beside the settings so that a
/// county looked at once opens at once the next time, and the service is not asked twice for the
/// same thing. The answers are kept as they came (the XML), one row per question; the user's
/// account is in the question as it is sent but not in the key here, and never in an answer.
/// </summary>
public sealed class RrCache : IDisposable
{
    private readonly SqliteConnection? _db;
    private readonly object _lock = new();

    /// <summary>The file in the program's own folder; <paramref name="path"/> is for tests (":memory:" keeps nothing).</summary>
    public RrCache(string? path = null)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleScanner", "radioreference.db");
        try
        {
            if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            _db.Open();
            Run("CREATE TABLE IF NOT EXISTS answers (question TEXT PRIMARY KEY, fetched INTEGER NOT NULL, xml TEXT NOT NULL)");
        }
        // Without the file the browser still works; it only asks the service every time.
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            _db?.Dispose();
            _db = null;
        }
    }

    /// <summary>The answer kept for this question, if there is one no older than <paramref name="longest"/>.</summary>
    public (string Xml, DateTime Fetched)? Get(string question, TimeSpan longest)
    {
        if (_db is null) return null;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT xml, fetched FROM answers WHERE question = $q";
                cmd.Parameters.AddWithValue("$q", question);
                using var row = cmd.ExecuteReader();
                if (!row.Read()) return null;
                var fetched = DateTimeOffset.FromUnixTimeSeconds(row.GetInt64(1)).UtcDateTime;
                return DateTime.UtcNow - fetched <= longest ? (row.GetString(0), fetched) : null;
            }
            catch (SqliteException) { return null; }
        }
    }

    public void Put(string question, string xml)
    {
        if (_db is null) return;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT INTO answers (question, fetched, xml) VALUES ($q, $t, $x) " +
                                  "ON CONFLICT(question) DO UPDATE SET fetched = $t, xml = $x";
                cmd.Parameters.AddWithValue("$q", question);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$x", xml);
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { }
        }
    }

    /// <summary>Every answer kept, of whatever age, to the questions that start with this text.</summary>
    public List<(string Question, string Xml)> All(string start)
    {
        var list = new List<(string, string)>();
        if (_db is null) return list;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT question, xml FROM answers WHERE substr(question, 1, $n) = $s ORDER BY fetched";
                cmd.Parameters.AddWithValue("$n", start.Length);
                cmd.Parameters.AddWithValue("$s", start);
                using var row = cmd.ExecuteReader();
                while (row.Read()) list.Add((row.GetString(0), row.GetString(1)));
            }
            catch (SqliteException) { }
        }
        return list;
    }

    /// <summary>Forget one answer, so that it is fetched again.</summary>
    public void Forget(string question)
    {
        if (_db is null) return;
        lock (_lock)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "DELETE FROM answers WHERE question = $q";
                cmd.Parameters.AddWithValue("$q", question);
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { }
        }
    }

    private void Run(string sql)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db?.Dispose();
}
