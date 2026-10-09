using System.Text.RegularExpressions;

namespace Resonance.Core;

public sealed partial class GenericImporter(HttpClient http) : IPlaylistImporter
{
    [GeneratedRegex(@"^File(\d+)=(.*)$", RegexOptions.IgnoreCase)] private static partial Regex PlsFile();
    public bool CanHandle(string input) => !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.IsFile || new[] { ".m3u", ".m3u8", ".pls", ".txt" }.Contains(Path.GetExtension(uri.AbsolutePath).ToLowerInvariant());
    public async Task<ResolvedPlaylist> ResolveAsync(string input, CancellationToken cancellationToken)
    {
        input = input.Trim();
        string text = input, name = "Imported playlist"; string? source = null; string? baseFolder = null; Uri? baseUri = null;
        if (File.Exists(input))
        {
            if (new FileInfo(input).Length > 4 * 1024 * 1024) throw new InvalidDataException("Playlist files must be smaller than 4 MB.");
            text = await File.ReadAllTextAsync(input, cancellationToken); name = Path.GetFileNameWithoutExtension(input); source = Path.GetFullPath(input); baseFolder = Path.GetDirectoryName(source);
        }
        else if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (bounded.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("Playlist exceeds 4 MB.");
                await bounded.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            text = System.Text.Encoding.UTF8.GetString(bounded.ToArray()); baseUri = uri; source = input; name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        }
        var items = Parse(text, baseFolder, baseUri);
        if (items.Count == 0) throw new InvalidDataException("No tracks found. Paste Artist - Title lines, or choose an M3U, M3U8, PLS or TXT file.");
        return new(name, "Generic", source, items, "Local files are matched to your index. Internet stream entries remain links; no audio is downloaded.");
    }
    public static IReadOnlyList<PlaylistItem> Parse(string text, string? baseFolder = null, Uri? baseUri = null)
    {
        var lines = text.TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
        if (lines.Length > 20000) throw new InvalidDataException("A playlist may contain at most 20,000 lines.");
        var isM3u = lines.Any(l => l.StartsWith("#EXT", StringComparison.OrdinalIgnoreCase));
        var isPls = lines.Any(l => l.Equals("[playlist]", StringComparison.OrdinalIgnoreCase));
        var items = new List<PlaylistItem>(); string? label = null;
        if (isPls)
        {
            var fields = lines.Where(l => l.Contains('=')).Select(l => l.Split('=', 2)).GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.OrdinalIgnoreCase);
            foreach (var field in lines.Select(l => PlsFile().Match(l)).Where(m => m.Success).OrderBy(m => int.Parse(m.Groups[1].Value)))
            {
                fields.TryGetValue("Title" + field.Groups[1].Value, out var title);
                items.Add(Entry(field.Groups[2].Value, title, baseFolder, baseUri));
            }
        }
        else foreach (var line in lines)
        {
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase)) { label = line.Contains(',') ? line[(line.IndexOf(',') + 1)..] : null; continue; }
            if (line.StartsWith('#') || line.StartsWith(';')) continue;
            var looksLikePath = LibraryScanner.Extensions.Contains(Path.GetExtension(line)) || line.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
            var isWeb = Uri.TryCreate(line, UriKind.Absolute, out var url) && url.Scheme is "http" or "https";
            if (isM3u || looksLikePath || isWeb) items.Add(Entry(line, label, baseFolder, baseUri));
            else
            {
                var (artist, title) = SplitLabel(line);
                items.Add(new() { ExternalArtist = artist, ExternalTitle = title, MatchStatus = MatchStatus.Link });
            }
            label = null;
        }
        return items.Select((item, i) => item with { Position = i }).ToArray();
    }
    private static PlaylistItem Entry(string value, string? label, string? baseFolder, Uri? baseUri)
    {
        string? local = null, external = null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") external = uri.AbsoluteUri;
        else if (baseUri is not null) external = new Uri(baseUri, value).AbsoluteUri;
        else
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.IsFile) local = uri.LocalPath;
            else local = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseFolder ?? Environment.CurrentDirectory, value));
        }
        var (artist, title) = SplitLabel(label ?? Path.GetFileNameWithoutExtension(local ?? value));
        return new() { ExternalTitle = title, ExternalArtist = artist, ExternalUrl = external, LocalPath = local, MatchStatus = MatchStatus.Link };
    }
    private static (string Artist, string Title) SplitLabel(string line)
    {
        var pair = line.Split(" - ", 2, StringSplitOptions.TrimEntries);
        return pair.Length == 2 ? (pair[0], pair[1]) : ("", line);
    }
}
