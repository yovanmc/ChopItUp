using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;
using ChopItUp.Core.Model;

namespace ChopItUp.Core.Tests.Storage;

public sealed class RoomModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_modes_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Fresh_rooms_persist_relay_and_the_ordered_default_pair()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        new MessageStore(db).CreateRoom("test", "Test", null);
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT mode, mode_first, mode_second, mode_revision FROM rooms WHERE id='test'";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("relay", reader.GetString(0));
        Assert.Equal("gpt-6-astra", reader.GetString(1));
        Assert.Equal("opus", reader.GetString(2));
        Assert.Equal(0, reader.GetInt64(3));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void V14_migration_preserves_messages_and_settings_survive_reopen()
    {
        var path = Path.Combine(_dir, "chopitup.db");
        var db = new ChopDb(path);
        db.EnsureDatabase();
        var store = new MessageStore(db);
        var imported = store.Import("general", "owner", "historical text");
        var objective = store.Post("general", "owner", "/objective retain this");
        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE rooms DROP COLUMN mode; ALTER TABLE rooms DROP COLUMN mode_first; ALTER TABLE rooms DROP COLUMN mode_second; ALTER TABLE rooms DROP COLUMN mode_revision; PRAGMA user_version=14;";
            command.ExecuteNonQuery();
        }
        new ChopDb(path).EnsureDatabase();
        Assert.Equal(15, db.GetSchemaVersion());
        Assert.Equal(new RoomModeSettings(), store.GetRoom("general")!.EffectiveMode);
        Assert.Contains(store.Read("general", 0, 100).Messages, m => m.Id == imported.Id && m.Imported);
        Assert.Equal(objective.Id, store.ReadSpawnContext("general", 60).Governing.Objective!.Source.Id);
        Assert.NotEmpty(Directory.GetFiles(_dir, "*.v14.*.bak"));
        Assert.True(store.SetMode("general", new RoomModeSettings("primary", "sonnet", null), ChopDb.SeedRoster));
        new ChopDb(path).EnsureDatabase();
        Assert.Equal(new RoomModeSettings("primary", "sonnet", null, 1), new MessageStore(new ChopDb(path)).GetRoom("general")!.EffectiveMode);
    }

    [Theory]
    [InlineData("/mode panel @sonnet")]
    [InlineData("/mode relay @sonnet @sonnet")]
    [InlineData("/mode primary @owner")]
    [InlineData("/mode primary @no-such-model")]
    [InlineData("/mode relay @sonnet @opus extra")]
    public void Invalid_mode_commands_do_not_produce_settings(string text) =>
        Assert.Throws<ArgumentException>(() => RoomModeSettings.Parse(text, new(), ChopDb.SeedRoster));
}
