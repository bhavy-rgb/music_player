namespace Resonance.Core;

public sealed record Track
{
    public long Id { get; init; }
    public string FilePath { get; init; } = "";
    public string Title { get; init; } = "Untitled";
    public string Artist { get; init; } = "Unknown artist";
    public string Album { get; init; } = "Unknown album";
    public string AlbumArtist { get; init; } = "";
    public long DurationMs { get; init; }
    public uint TrackNumber { get; init; }
    public uint Year { get; init; }
    public string Genre { get; init; } = "";
    public string? ArtPath { get; init; }
    public DateTimeOffset DateAdded { get; init; } = DateTimeOffset.UtcNow;
    public long FileSize { get; init; }
    public long ModifiedTicks { get; init; }
    public string DurationLabel => TimeSpan.FromMilliseconds(DurationMs).ToString(DurationMs >= 3600000 ? @"h\:mm\:ss" : @"m\:ss");
    public string Folder => Path.GetDirectoryName(FilePath) ?? "";
}
public enum MatchStatus { Link, Local, Preview }
public enum PlaybackMode { Sequential, RepeatOne, RepeatAll, Shuffle }
public sealed record PlaylistItem
{
    public long Id { get; init; }
    public long PlaylistId { get; init; }
    public int Position { get; init; }
    public long? TrackId { get; init; }
    public string ExternalTitle { get; init; } = "";
    public string ExternalArtist { get; init; } = "";
    public string ExternalAlbum { get; init; } = "";
    public long DurationMs { get; init; }
    public string? ArtUrl { get; init; }
    public string? ExternalUrl { get; init; }
    public string? PreviewUrl { get; init; }
    public string? LocalPath { get; init; }
    public MatchStatus MatchStatus { get; init; }
    public double Confidence { get; init; }
}
public sealed record Playlist
{
    public long Id { get; init; }
    public string Name { get; init; } = "New playlist";
    public string SourceProvider { get; init; } = "Local";
    public string? SourceUrl { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncedAt { get; init; }
    public IReadOnlyList<PlaylistItem> Items { get; init; } = [];
}
public sealed record ResolvedPlaylist(string Name, string Provider, string? SourceUrl, IReadOnlyList<PlaylistItem> Items, string? Notice = null);
public sealed record ScanProgress(int Count, string CurrentPath);
public sealed record ScanResult(IReadOnlyList<Track> Tracks, IReadOnlySet<string> EnumeratedFolders, IReadOnlySet<string> SeenFiles, IReadOnlyList<string> Errors);
public interface ILibraryScanner
{
    Task<ScanResult> ScanFilesAsync(IEnumerable<string> paths, IReadOnlyList<Track> existing, CancellationToken cancellationToken);
    Task<ScanResult> ScanAsync(IEnumerable<string> roots, IReadOnlyList<Track> existing, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}
public interface ILibraryStore
{
    Task InitializeAsync();
    Task<IReadOnlyList<Track>> GetTracksAsync();
    Task ApplyScanAsync(ScanResult scan);
    Task<IReadOnlyList<Playlist>> GetPlaylistsAsync();
    Task<long> SavePlaylistAsync(Playlist playlist);
    Task DeletePlaylistAsync(long id);
    Task<string?> GetSettingAsync(string key);
    Task SetSettingAsync(string key, string value);
}
public interface IPlaylistImporter
{
    bool CanHandle(string input);
    Task<ResolvedPlaylist> ResolveAsync(string input, CancellationToken cancellationToken);
}
public interface IPlaybackEngine : IDisposable
{
    event EventHandler? Ended;
    event EventHandler<string>? Failed;
    bool IsPlaying { get; }
    TimeSpan Position { get; set; }
    TimeSpan Duration { get; }
    float Volume { get; set; }
    Task OpenAsync(string path);
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
}
public sealed class ImportConfigurationException(string message) : Exception(message);
