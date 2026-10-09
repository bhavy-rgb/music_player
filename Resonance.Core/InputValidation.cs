using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resonance.Core;

public static class InputValidation
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MaxItems = 20000;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (data.Length + count > MaxBytes) throw new InvalidDataException("Content exceeds the 4 MB safety limit.");
            await data.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return data.ToArray();
    }

    public static void Text(string? value, int max = 4096)
    {
        if (value is null || value.Length > max || value.Contains('\0'))
            throw new InvalidDataException("Metadata contains a missing or oversized text field.");
    }

    public static void Item(PlaylistItem item)
    {
        Text(item.ExternalTitle); Text(item.ExternalArtist); Text(item.ExternalAlbum);
        foreach (var value in new[] { item.ExternalUrl, item.PreviewUrl, item.ArtUrl, item.LocalPath })
            if (value is not null) Text(value, 8192);
        if (item.DurationMs is < 0 or > 31536000000L || !double.IsFinite(item.Confidence)
            || item.Confidence is < 0 or > 1 || !Enum.IsDefined(item.MatchStatus))
            throw new InvalidDataException("Playlist metadata contains an invalid duration or match value.");
    }

    public static void Items(IReadOnlyList<PlaylistItem> items)
    {
        if (items.Count > MaxItems) throw new InvalidDataException("A playlist may contain at most 20,000 tracks.");
        foreach (var item in items) Item(item);
    }

    public static void AudioPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !LibraryScanner.Extensions.Contains(Path.GetExtension(path)))
            throw new InvalidDataException("Only local audio files can be indexed or played.");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links and reparse points are not followed.");
    }
}
