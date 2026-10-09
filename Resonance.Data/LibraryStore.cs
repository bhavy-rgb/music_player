using Microsoft.Data.Sqlite;
using Resonance.Core;
using System.Text.Json;

namespace Resonance.Data;

/// <summary>All database I/O is serialized off the UI thread. Every write uses a transaction.</summary>
public sealed class LibraryStore(string databasePath) : ILibraryStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private async Task<T> WithDatabase<T>(Func<SqliteConnection, T> work)
    {
        await gate.WaitAsync();
        try
        {
            return await Task.Run(() => {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true }.ToString());
                db.Open(); return work(db);
            });
        }
        finally { gate.Release(); }
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? tx = null, params (string, object?)[] values)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    public Task InitializeAsync() => WithDatabase(db => {
        using var cmd = Command(db, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Track (
              Id INTEGER PRIMARY KEY, FilePath TEXT NOT NULL COLLATE NOCASE UNIQUE,
              Title TEXT NOT NULL, Artist TEXT NOT NULL, Album TEXT NOT NULL,
              AlbumArtist TEXT NOT NULL, DurationMs INTEGER NOT NULL, TrackNumber INTEGER NOT NULL,
              Year INTEGER NOT NULL, Genre TEXT NOT NULL, ArtPath TEXT, DateAdded TEXT NOT NULL,
              FileSize INTEGER NOT NULL, ModifiedTicks INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Track_Artist ON Track(Artist);
            CREATE INDEX IF NOT EXISTS IX_Track_Album ON Track(Album);
            CREATE TABLE IF NOT EXISTS Playlist (
              Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, SourceProvider TEXT NOT NULL,
              SourceUrl TEXT, CreatedAt TEXT NOT NULL, LastSyncedAt TEXT);
            CREATE TABLE IF NOT EXISTS PlaylistItem (
              Id INTEGER PRIMARY KEY, PlaylistId INTEGER NOT NULL REFERENCES Playlist(Id) ON DELETE CASCADE,
              Position INTEGER NOT NULL, TrackId INTEGER REFERENCES Track(Id) ON DELETE SET NULL,
              Snapshot TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_PlaylistItem_Order ON PlaylistItem(PlaylistId, Position);
            CREATE TABLE IF NOT EXISTS Setting (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            PRAGMA user_version=1;
            """); cmd.ExecuteNonQuery(); return true;
    });
    public Task<IReadOnlyList<Track>> GetTracksAsync() => WithDatabase<IReadOnlyList<Track>>(ReadTracks);
    private static List<Track> ReadTracks(SqliteConnection db)
    {
        using var cmd = Command(db, "SELECT Id,FilePath,Title,Artist,Album,AlbumArtist,DurationMs,TrackNumber,Year,Genre,ArtPath,DateAdded,FileSize,ModifiedTicks FROM Track ORDER BY Artist,Album,TrackNumber,Title");
        using var r = cmd.ExecuteReader(); var tracks = new List<Track>();
        while (r.Read()) tracks.Add(new() { Id = r.GetInt64(0), FilePath = r.GetString(1), Title = r.GetString(2), Artist = r.GetString(3), Album = r.GetString(4), AlbumArtist = r.GetString(5), DurationMs = r.GetInt64(6), TrackNumber = (uint)r.GetInt64(7), Year = (uint)r.GetInt64(8), Genre = r.GetString(9), ArtPath = r.IsDBNull(10) ? null : r.GetString(10), DateAdded = DateTimeOffset.Parse(r.GetString(11)), FileSize = r.GetInt64(12), ModifiedTicks = r.GetInt64(13) });
        return tracks;
    }
    public Task ApplyScanAsync(ScanResult scan) => WithDatabase(db => {
        var old = ReadTracks(db); using var tx = db.BeginTransaction();
        foreach (var t in scan.Tracks)
        {
            using var cmd = Command(db, """
              INSERT INTO Track(FilePath,Title,Artist,Album,AlbumArtist,DurationMs,TrackNumber,Year,Genre,ArtPath,DateAdded,FileSize,ModifiedTicks)
              VALUES($path,$title,$artist,$album,$albumartist,$duration,$number,$year,$genre,$art,$added,$size,$modified)
              ON CONFLICT(FilePath) DO UPDATE SET Title=excluded.Title,Artist=excluded.Artist,Album=excluded.Album,
              AlbumArtist=excluded.AlbumArtist,DurationMs=excluded.DurationMs,TrackNumber=excluded.TrackNumber,
              Year=excluded.Year,Genre=excluded.Genre,ArtPath=excluded.ArtPath,FileSize=excluded.FileSize,ModifiedTicks=excluded.ModifiedTicks
              """, tx, ("$path", t.FilePath), ("$title", t.Title), ("$artist", t.Artist), ("$album", t.Album), ("$albumartist", t.AlbumArtist), ("$duration", t.DurationMs), ("$number", t.TrackNumber), ("$year", t.Year), ("$genre", t.Genre), ("$art", t.ArtPath), ("$added", t.DateAdded.ToString("O")), ("$size", t.FileSize), ("$modified", t.ModifiedTicks));
            cmd.ExecuteNonQuery();
        }
        foreach (var t in old.Where(t => scan.EnumeratedFolders.Contains(t.Folder) && !scan.SeenFiles.Contains(t.FilePath)))
        {
            using var cmd = Command(db, "DELETE FROM Track WHERE Id=$id", tx, ("$id", t.Id)); cmd.ExecuteNonQuery();
        }
        tx.Commit(); return true;
    });
    public Task<IReadOnlyList<Playlist>> GetPlaylistsAsync() => WithDatabase<IReadOnlyList<Playlist>>(db => {
        var playlists = new List<Playlist>();
        using (var cmd = Command(db, "SELECT Id,Name,SourceProvider,SourceUrl,CreatedAt,LastSyncedAt FROM Playlist ORDER BY Name"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) playlists.Add(new() { Id = r.GetInt64(0), Name = r.GetString(1), SourceProvider = r.GetString(2), SourceUrl = r.IsDBNull(3) ? null : r.GetString(3), CreatedAt = DateTimeOffset.Parse(r.GetString(4)), LastSyncedAt = r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5)) });
        }
        for (var i = 0; i < playlists.Count; i++)
        {
            using var cmd = Command(db, "SELECT Id,Position,TrackId,Snapshot FROM PlaylistItem WHERE PlaylistId=$id ORDER BY Position", null, ("$id", playlists[i].Id));
            using var r = cmd.ExecuteReader(); var items = new List<PlaylistItem>();
            while (r.Read())
            {
                var snapshot = r.GetString(3);
                InputValidation.Text(snapshot, 65536);
                var item = JsonSerializer.Deserialize<PlaylistItem>(snapshot, InputValidation.JsonOptions)
                    ?? throw new InvalidDataException("A saved playlist entry is invalid.");
                InputValidation.Item(item);
                long? trackId = r.IsDBNull(2) ? null : r.GetInt64(2);
                items.Add(item with { Id = r.GetInt64(0), PlaylistId = playlists[i].Id, Position = r.GetInt32(1), TrackId = trackId, MatchStatus = trackId.HasValue ? MatchStatus.Local : item.PreviewUrl is null ? MatchStatus.Link : MatchStatus.Preview });
            }
            playlists[i] = playlists[i] with { Items = items };
        }
        return playlists;
    });
    public Task<long> SavePlaylistAsync(Playlist playlist) => WithDatabase(db => {
        InputValidation.Text(playlist.Name, 256); InputValidation.Text(playlist.SourceProvider, 64);
        if (string.IsNullOrWhiteSpace(playlist.Name)) throw new InvalidDataException("Give the playlist a name.");
        if (playlist.SourceUrl is not null) InputValidation.Text(playlist.SourceUrl, 8192);
        InputValidation.Items(playlist.Items);
        using var tx = db.BeginTransaction();
        var id = playlist.Id;
        if (id == 0)
        {
            using var cmd = Command(db, "INSERT INTO Playlist(Name,SourceProvider,SourceUrl,CreatedAt,LastSyncedAt) VALUES($name,$provider,$url,$created,$synced) RETURNING Id", tx, ("$name", playlist.Name), ("$provider", playlist.SourceProvider), ("$url", playlist.SourceUrl), ("$created", playlist.CreatedAt.ToString("O")), ("$synced", playlist.LastSyncedAt?.ToString("O")));
            id = (long)cmd.ExecuteScalar()!;
        }
        else
        {
            using var cmd = Command(db, "UPDATE Playlist SET Name=$name,SourceProvider=$provider,SourceUrl=$url,LastSyncedAt=$synced WHERE Id=$id", tx, ("$id", id), ("$name", playlist.Name), ("$provider", playlist.SourceProvider), ("$url", playlist.SourceUrl), ("$synced", playlist.LastSyncedAt?.ToString("O")));
            cmd.ExecuteNonQuery();
        }
        using (var cmd = Command(db, "DELETE FROM PlaylistItem WHERE PlaylistId=$id", tx, ("$id", id))) cmd.ExecuteNonQuery();
        for (var i = 0; i < playlist.Items.Count; i++)
        {
            var item = playlist.Items[i];
            using var cmd = Command(db, "INSERT INTO PlaylistItem(PlaylistId,Position,TrackId,Snapshot) VALUES($id,$position,$track,$snapshot)", tx, ("$id", id), ("$position", i), ("$track", item.TrackId), ("$snapshot", JsonSerializer.Serialize(item)));
            cmd.ExecuteNonQuery();
        }
        tx.Commit(); return id;
    });
    public Task DeletePlaylistAsync(long id) => WithDatabase(db => {
        using var tx = db.BeginTransaction();
        using var cmd = Command(db, "DELETE FROM Playlist WHERE Id=$id", tx, ("$id", id)); var count = cmd.ExecuteNonQuery(); tx.Commit(); return count;
    });
    public Task<string?> GetSettingAsync(string key) => WithDatabase(db => {
        using var cmd = Command(db, "SELECT Value FROM Setting WHERE Key=$key", null, ("$key", key)); return cmd.ExecuteScalar() as string;
    });
    public Task SetSettingAsync(string key, string value) => WithDatabase(db => {
        InputValidation.Text(key, 128); InputValidation.Text(value, InputValidation.MaxBytes);
        using var tx = db.BeginTransaction();
        using var cmd = Command(db, "INSERT INTO Setting(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", tx, ("$key", key), ("$value", value)); var count = cmd.ExecuteNonQuery(); tx.Commit(); return count;
    });
}
