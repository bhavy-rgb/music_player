using System.Security.Cryptography;
using System.Text;

namespace Resonance.Core;

public sealed class LibraryScanner(string artDirectory) : ILibraryScanner
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".ogg" };
    public Task<ScanResult> ScanAsync(IEnumerable<string> roots, IReadOnlyList<Track> existing, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Scan(roots, existing, progress, cancellationToken), cancellationToken);

    public Task<ScanResult> ScanFilesAsync(IEnumerable<string> paths, IReadOnlyList<Track> existing, CancellationToken cancellationToken)
        => Task.Run(() => {
            Directory.CreateDirectory(artDirectory);
            var tracks = new List<Track>(); var errors = new List<string>();
            var cache = existing.ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    InputValidation.AudioPath(path);
                    cache.TryGetValue(path, out var old);
                    var info = new FileInfo(path);
                    tracks.Add(old is not null && old.FileSize == info.Length && old.ModifiedTicks == info.LastWriteTimeUtc.Ticks
                        ? old : ReadTrack(path, info, old));
                }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{path}: {e.Message}"); }
            }
            return new ScanResult(tracks, new HashSet<string>(), new HashSet<string>(), errors);
        }, cancellationToken);

    private ScanResult Scan(IEnumerable<string> roots, IReadOnlyList<Track> existing, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(artDirectory);
        var cached = existing.ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);
        var tracks = new List<Track>();
        var errors = new List<string>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots.Where(Directory.Exists).Select(Path.GetFullPath));
        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(folder)) continue;
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                // Complete enumeration before treating a directory as authoritative for deletions.
                var files = Directory.GetFiles(folder);
                folders.Add(folder);
                foreach (var path in files.Where(p => Extensions.Contains(Path.GetExtension(p))))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seen.Add(path)) continue;
                    try
                    {
                        InputValidation.AudioPath(path);
                        var info = new FileInfo(path);
                        cached.TryGetValue(path, out var old);
                        if (old is not null && old.ModifiedTicks == info.LastWriteTimeUtc.Ticks && old.FileSize == info.Length)
                            tracks.Add(old);
                        else tracks.Add(ReadTrack(path, info, old));
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{Path.GetFileName(path)}: {e.Message}"); }
                    if (seen.Count % 20 == 0) progress?.Report(new(seen.Count, path));
                }
                foreach (var sub in Directory.GetDirectories(folder))
                {
                    try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) pending.Push(sub); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add($"{sub}: {e.Message}"); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add($"{folder}: {e.Message}"); }
        }
        progress?.Report(new(seen.Count, "Scan complete"));
        return new(tracks, folders, seen, errors);
    }
    private Track ReadTrack(string path, FileInfo info, Track? old)
    {
        InputValidation.AudioPath(path);
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        string? art = null;
        if (tag.Pictures.FirstOrDefault() is { } picture && picture.Data.Count <= 12 * 1024 * 1024)
        {
            var bytes = picture.Data.Data;
            var key = Convert.ToHexString(SHA256.HashData(bytes));
            art = Path.Combine(artDirectory, key + ".img");
            if (!File.Exists(art)) File.WriteAllBytes(art, bytes);
        }
        foreach (var value in new[] { tag.Title ?? "", tag.Album ?? "", string.Join(", ", tag.Performers), string.Join(", ", tag.AlbumArtists), string.Join(", ", tag.Genres) })
            InputValidation.Text(value);
        return new Track {
            Id = old?.Id ?? 0, FilePath = path, Title = string.IsNullOrWhiteSpace(tag.Title) ? Path.GetFileNameWithoutExtension(path) : tag.Title,
            Artist = tag.Performers.Length == 0 ? "Unknown artist" : string.Join(", ", tag.Performers),
            Album = string.IsNullOrWhiteSpace(tag.Album) ? "Unknown album" : tag.Album,
            AlbumArtist = string.Join(", ", tag.AlbumArtists), DurationMs = (long)file.Properties.Duration.TotalMilliseconds,
            TrackNumber = tag.Track, Year = tag.Year, Genre = string.Join(", ", tag.Genres), ArtPath = art,
            DateAdded = old?.DateAdded ?? DateTimeOffset.UtcNow, FileSize = info.Length, ModifiedTicks = info.LastWriteTimeUtc.Ticks
        };
    }
}
