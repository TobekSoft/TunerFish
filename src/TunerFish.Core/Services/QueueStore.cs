using Microsoft.Data.Sqlite;

namespace TunerFish.Services;

/// <summary>
/// One saved queue entry: enough to show it before anything is fetched, and to rebuild its stream and
/// cover URLs on whichever server address is in use next time (so never a URL itself).
/// </summary>
public sealed record SavedQueueEntry(
    string TrackId, string Title, string Artist, double? Seconds,
    string? AlbumId, string? AlbumTitle, string? ImageTag);

/// <summary>
/// The play queue, kept across restarts (the place in it lives in settings, like the Android app's).
/// Lives in ~/.local/share/TunerFish/tunerfish.db (<see cref="AppPaths"/>): it's your data, not a cache.
/// </summary>
public sealed class QueueStore
{
    // 1 was the retired sort_override table (sort names before filing followed Jellyfin's); left in place, unused.
    private const int SchemaVersion = 2;
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public QueueStore(string? databasePath = null)
    {
        DatabasePath = databasePath ?? AppPaths.Current.DatabaseFile;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        // No pooling: nothing here is busy enough to need it, and a pooled connection keeps the file open.
        _connectionString = $"Data Source={DatabasePath};Pooling=False";
        Migrate();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(check.ExecuteScalar());
        if (version >= SchemaVersion) return;

        using var create = connection.CreateCommand();
        create.CommandText =
            """
            CREATE TABLE IF NOT EXISTS queue_item (
                position    INTEGER PRIMARY KEY,
                track_id    TEXT NOT NULL,
                title       TEXT NOT NULL,
                artist      TEXT NOT NULL,
                seconds     REAL,
                album_id    TEXT,
                album_title TEXT,
                image_tag   TEXT
            );
            PRAGMA user_version = 2;
            """;
        create.ExecuteNonQuery();
    }

    public List<SavedQueueEntry> Load()
    {
        var result = new List<SavedQueueEntry>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT track_id, title, artist, seconds, album_id, album_title, image_tag FROM queue_item ORDER BY position;";
        using var reader = command.ExecuteReader();
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        while (reader.Read())
            result.Add(new SavedQueueEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                                           reader.IsDBNull(3) ? null : reader.GetDouble(3), Text(4), Text(5), Text(6)));
        return result;
    }

    /// <summary>Replaces the saved queue, all or nothing.</summary>
    public void Save(IReadOnlyList<SavedQueueEntry> entries)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM queue_item;";
            clear.ExecuteNonQuery();
        }
        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO queue_item (position, track_id, title, artist, seconds, album_id, album_title, image_tag)
            VALUES ($position, $track, $title, $artist, $seconds, $album, $albumTitle, $tag);
            """;
        var p = new[] { "$position", "$track", "$title", "$artist", "$seconds", "$album", "$albumTitle", "$tag" }
            .Select(name => insert.Parameters.Add(new SqliteParameter { ParameterName = name })).ToArray();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            object?[] values = { i, e.TrackId, e.Title, e.Artist, e.Seconds, e.AlbumId, e.AlbumTitle, e.ImageTag };
            for (var j = 0; j < p.Length; j++) p[j].Value = values[j] ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
