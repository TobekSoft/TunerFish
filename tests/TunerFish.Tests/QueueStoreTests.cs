using Microsoft.Data.Sqlite;
using TunerFish.Services;
using TunerFish.Tests.Support;

namespace TunerFish.Tests;

public sealed class QueueStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string DbPath => _dir.File("data", "tunerfish.db");

    public void Dispose() => _dir.Dispose();

    private static readonly SavedQueueEntry Taxman = new("t1", "Taxman", "The Beatles", 158.5, "al2", "Revolver", "tagA");
    private static readonly SavedQueueEntry Loose = new("x9", "A Loose Track", "Someone", null, null, null, null);

    [Fact]
    public void TheQueue_ComesBackInOrder_WithItsGaps()
    {
        new QueueStore(DbPath).Save(new[] { Taxman, Loose, Taxman });

        Assert.Equal(new[] { Taxman, Loose, Taxman }, new QueueStore(DbPath).Load());
    }

    [Fact]
    public void Saving_ReplacesTheWholeQueue()
    {
        var store = new QueueStore(DbPath);
        store.Save(new[] { Taxman, Loose });

        store.Save(new[] { Loose });
        Assert.Equal(new[] { Loose }, store.Load());

        store.Save(Array.Empty<SavedQueueEntry>());
        Assert.Empty(store.Load());
    }

    [Fact]
    public void ADatabaseFromBeforeTheQueue_IsUpgraded_AndItsOldTableLeftAlone()
    {
        // What 0.8 left behind: schema version 1, with your old sort names.
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using (var c = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE sort_override (kind TEXT, name TEXT, sort_key TEXT); " +
                              "INSERT INTO sort_override VALUES ('artist', 'Elvis Costello', 'Costello'); PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }

        var store = new QueueStore(DbPath);
        store.Save(new[] { Taxman });

        Assert.Equal(new[] { Taxman }, store.Load());
        using var check = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM sort_override; ";
        Assert.Equal(1L, q.ExecuteScalar());
        q.CommandText = "PRAGMA user_version;";
        Assert.Equal(2L, q.ExecuteScalar());
    }
}
