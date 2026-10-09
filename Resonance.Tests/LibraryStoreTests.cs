using Microsoft.Data.Sqlite;
using Resonance.Core;
using Resonance.Data;
using System.Text.Json;
using Xunit;

namespace Resonance.Tests;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly TemporaryDirectory temp = new();
    private string Database => temp.FilePath("library.db");
    private LibraryStore Store => new(Database);
    private SqliteConnection Connection() => new(new SqliteConnectionStringBuilder { DataSource = Database, ForeignKeys = true }.ToString());
    private Track Song(string relative = "music/song.mp3") => new()
    {
        FilePath = temp.FilePath(relative), Title = "Song", Artist = "Artist", Album = "Album", AlbumArtist = "Album Artist",
        DurationMs = 180000, TrackNumber = 2, Year = 2024, Genre = "Rock", ArtPath = temp.FilePath("art/cover.img"),
        DateAdded = DateTimeOffset.Parse("2024-01-02T03:04:05+00:00"), FileSize = 123, ModifiedTicks = 456
    };
    private static ScanResult Scan(params Track[] tracks) => new(tracks,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), tracks.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase), []);

    [Fact]
    public async Task InitializeCreatesSchemaWalAndCanReopenVersionZeroWithoutDataLoss()
    {
        var store = Store; await store.InitializeAsync(); await store.SetSettingAsync("existing", "value");
        await store.ApplyScanAsync(Scan(Song()));
        using (var db = Connection())
        {
            await db.OpenAsync(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                var names = new List<string>(); while (await reader.ReadAsync()) names.Add(reader.GetString(0));
                Assert.Equal(new[] { "Playlist", "PlaylistItem", "Setting", "Track" }, names);
            }
            cmd.CommandText = "PRAGMA journal_mode"; Assert.Equal("wal", (string?)await cmd.ExecuteScalarAsync());
            cmd.CommandText = "PRAGMA user_version=0"; await cmd.ExecuteNonQueryAsync();
        }
        await store.InitializeAsync(); await Store.InitializeAsync();
        Assert.Single(await Store.GetTracksAsync()); Assert.Equal("value", await Store.GetSettingAsync("existing"));
        using var reopened = Connection(); await reopened.OpenAsync(); using var version = reopened.CreateCommand();
        version.CommandText = "PRAGMA user_version"; Assert.Equal(1L, await version.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ApplyScanInsertsAndUpdatesInPlacePreservingIdDateAndPlaylistReference()
    {
        var store = Store; await store.InitializeAsync(); var original = Song(); await store.ApplyScanAsync(Scan(original));
        var saved = Assert.Single(await store.GetTracksAsync());
        Assert.True(saved.Id > 0); Assert.Equal(original with { Id = saved.Id }, saved);
        await store.SavePlaylistAsync(new() { Name = "Saved", Items = [new() { TrackId = saved.Id, ExternalTitle = "Song" }] });
        await store.ApplyScanAsync(Scan(original with { Title = "Retagged", FileSize = 999, ModifiedTicks = 888, DateAdded = DateTimeOffset.UtcNow }));
        var updated = Assert.Single(await store.GetTracksAsync());
        Assert.Equal(saved.Id, updated.Id); Assert.Equal(saved.DateAdded, updated.DateAdded);
        Assert.Equal("Retagged", updated.Title); Assert.Equal(999, updated.FileSize); Assert.Equal(888, updated.ModifiedTicks);
        Assert.Equal(saved.Id, Assert.Single(Assert.Single(await store.GetPlaylistsAsync()).Items).TrackId);
    }

    [Fact]
    public async Task PrunesOnlyMissingFilesInExactlyEnumeratedFolders()
    {
        var store = Store; await store.InitializeAsync();
        var missing = Song("scanned/missing.mp3"); var unreadable = Song("scanned/unreadable.mp3");
        var offline = Song("offline/song.mp3"); var nested = Song("scanned/unvisited/song.mp3");
        var neighbor = Song("scanned-other/song.mp3");
        await store.ApplyScanAsync(Scan(missing, unreadable, offline, nested, neighbor));
        await store.ApplyScanAsync(new([], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { missing.Folder },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { unreadable.FilePath }, ["tag read failed"]));
        var paths = (await store.GetTracksAsync()).Select(t => t.FilePath).ToHashSet();
        Assert.Equal(4, paths.Count); Assert.DoesNotContain(missing.FilePath, paths);
        foreach (var track in new[] { unreadable, offline, nested, neighbor }) Assert.Contains(track.FilePath, paths);
    }

    [Fact]
    public async Task PlaylistSnapshotRoundTripsAndDatabaseOwnsIdsAndPositions()
    {
        var store = Store; await store.InitializeAsync(); await store.ApplyScanAsync(Scan(Song()));
        var track = Assert.Single(await store.GetTracksAsync());
        var item = new PlaylistItem { Id = 999, PlaylistId = 999, Position = 99, TrackId = track.Id,
            ExternalTitle = "L'été ' live", ExternalArtist = "Artist", ExternalAlbum = "Album", DurationMs = 180000,
            ArtUrl = "https://example.test/art.jpg", ExternalUrl = "https://example.test/song", PreviewUrl = "https://example.test/preview",
            LocalPath = track.FilePath, MatchStatus = MatchStatus.Local, Confidence = .95 };
        var playlist = new Playlist { Name = "Evening ' mix", SourceProvider = "Generic", SourceUrl = temp.FilePath("mix.m3u"),
            CreatedAt = track.DateAdded, LastSyncedAt = track.DateAdded.AddDays(1), Items = [item, new() { ExternalTitle = "Unmatched" }] };
        var id = await store.SavePlaylistAsync(playlist);
        var loaded = Assert.Single(await Store.GetPlaylistsAsync());
        Assert.Equal(id, loaded.Id); Assert.Equal(playlist.Name, loaded.Name); Assert.Equal(playlist.SourceProvider, loaded.SourceProvider);
        Assert.Equal(playlist.SourceUrl, loaded.SourceUrl); Assert.Equal(playlist.CreatedAt, loaded.CreatedAt); Assert.Equal(playlist.LastSyncedAt, loaded.LastSyncedAt);
        Assert.Equal(2, loaded.Items.Count); var actual = loaded.Items[0];
        Assert.True(actual.Id > 0); Assert.NotEqual(999, actual.Id);
        Assert.Equal(item with { Id = actual.Id, PlaylistId = id, Position = 0 }, actual);
        Assert.Equal(1, loaded.Items[1].Position); Assert.Equal(MatchStatus.Link, loaded.Items[1].MatchStatus);
        Assert.Equal(id, await store.SavePlaylistAsync(loaded with { Name = "Updated", Items = [loaded.Items[1]] }));
        var updated = Assert.Single(await store.GetPlaylistsAsync());
        Assert.Equal("Updated", updated.Name); Assert.Equal(0, Assert.Single(updated.Items).Position);
        await store.DeletePlaylistAsync(id); Assert.Empty(await store.GetPlaylistsAsync());
        using var db = Connection(); await db.OpenAsync(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM PlaylistItem"; Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(null, MatchStatus.Link)]
    [InlineData("https://example.test/preview", MatchStatus.Preview)]
    public async Task PrunedTrackClearsSnapshotReferenceAndDowngradesMatch(string? preview, MatchStatus expected)
    {
        var store = Store; await store.InitializeAsync(); await store.ApplyScanAsync(Scan(Song()));
        var track = Assert.Single(await store.GetTracksAsync());
        await store.SavePlaylistAsync(new() { Name = "Keep reference", Items = [new() { TrackId = track.Id, PreviewUrl = preview, MatchStatus = MatchStatus.Local }] });
        await store.ApplyScanAsync(new([], new HashSet<string> { track.Folder }, new HashSet<string>(), []));
        var item = Assert.Single(Assert.Single(await store.GetPlaylistsAsync()).Items);
        Assert.Null(item.TrackId); Assert.Equal(expected, item.MatchStatus);
    }

    [Theory]
    [InlineData("{\"Unexpected\":true}")]
    [InlineData("null")]
    [InlineData("{\"Confidence\":2}")]
    public async Task CorruptSnapshotIsRejectedInsteadOfTrusted(string json)
    {
        var store = Store; await store.InitializeAsync(); await store.SavePlaylistAsync(new() { Name = "Mix", Items = [new()] });
        using (var db = Connection())
        {
            await db.OpenAsync(); using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE PlaylistItem SET Snapshot=$snapshot"; cmd.Parameters.AddWithValue("$snapshot", json); await cmd.ExecuteNonQueryAsync();
        }
        var error = await Record.ExceptionAsync(() => store.GetPlaylistsAsync());
        Assert.True(error is JsonException or InvalidDataException, $"Expected validation failure, got {error?.GetType().Name ?? "success"}.");
    }

    [Fact]
    public async Task FailedPlaylistWriteRollsBackExistingPlaylistAndItems()
    {
        var store = Store; await store.InitializeAsync();
        var id = await store.SavePlaylistAsync(new() { Name = "Original", Items = [new() { ExternalTitle = "Keep" }] });
        await Assert.ThrowsAsync<SqliteException>(() => store.SavePlaylistAsync(new() { Id = id, Name = "Invalid update", Items = [new() { TrackId = 123456 }] }));
        var playlist = Assert.Single(await store.GetPlaylistsAsync());
        Assert.Equal("Original", playlist.Name); Assert.Equal("Keep", Assert.Single(playlist.Items).ExternalTitle);
    }

    [Fact]
    public async Task SettingsRoundTripUpsertAndTreatSqlAsData()
    {
        var store = Store; await store.InitializeAsync(); Assert.Null(await store.GetSettingAsync("missing"));
        const string key = "'; DROP TABLE Setting;--";
        await store.SetSettingAsync(key, "first"); await store.SetSettingAsync(key, "{\"queue\":[1,2],\"volume\":0.7}");
        Assert.Equal("{\"queue\":[1,2],\"volume\":0.7}", await Store.GetSettingAsync(key));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SetSettingAsync($"key{i}", i.ToString())));
        for (var i = 0; i < 20; i++) Assert.Equal(i.ToString(), await store.GetSettingAsync($"key{i}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SetSettingAsync(new string('x', 129), "invalid"));
    }

    public void Dispose()
    {
        using var connection = Connection(); SqliteConnection.ClearPool(connection);
        temp.Dispose();
    }
}
