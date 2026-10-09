using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Resonance.Core;

public interface IProviderCredentials
{
    string YouTubeApiKey { get; }
    string AppleDeveloperToken { get; }
    string SoundCloudAccessToken { get; }
    Task<string> GetSpotifyTokenAsync(CancellationToken ct);
}
public static class ProviderUrls
{
    public static bool IsHost(string input, params string[] hosts) => Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    public static string? Query(Uri uri, string key) => uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] == key).Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
    public static string Text(this JsonElement element, string key, string fallback = "")
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Provider returned an unexpected text field.");
        var text = value.GetString()!;
        InputValidation.Text(text, 8192);
        return text;
    }
    public static JsonElement Child(this JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;
    public static IEnumerable<JsonElement> Array(this JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
    public static long Number(this JsonElement element, string key) => element.Child(key).TryNumber();
    private static long TryNumber(this JsonElement element) => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var n) ? n : 0;
}
public sealed class ProviderApi(HttpClient http)
{
    public async Task<JsonElement> GetAsync(string url, string? token, CancellationToken ct, string scheme = "Bearer")
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || !new[] { "api.spotify.com", "www.googleapis.com", "api.music.apple.com", "api.soundcloud.com" }.Contains(uri.Host))
            throw new InvalidDataException("The provider returned an unexpected API address.");
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 2)
            {
                var seconds = Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 2 * (attempt + 1), 1, 20);
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct); continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Provider API returned {(int)response.StatusCode} ({response.ReasonPhrase}). Check credentials, developer-account access and whether the playlist is public. No audio has been downloaded.");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var document = JsonDocument.Parse(await InputValidation.ReadBoundedAsync(body, ct), new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Provider returned an unexpected response shape.");
            return document.RootElement.Clone();
        }
    }
}
public sealed class SpotifyImporter(ProviderApi api, IProviderCredentials credentials) : IPlaylistImporter
{
    public bool CanHandle(string input) => ProviderUrls.IsHost(input, "open.spotify.com");
    public async Task<ResolvedPlaylist> ResolveAsync(string input, CancellationToken cancellationToken)
    {
        var parts = new Uri(input).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var typeIndex = System.Array.FindIndex(parts, p => p is "playlist" or "album" or "track");
        if (typeIndex < 0 || typeIndex + 1 >= parts.Length) throw new InvalidDataException("Use a Spotify playlist, album or track URL.");
        var type = parts[typeIndex]; var id = Uri.EscapeDataString(parts[typeIndex + 1]);
        var token = await credentials.GetSpotifyTokenAsync(cancellationToken);
        var root = await api.GetAsync($"https://api.spotify.com/v1/{type}s/{id}", token, cancellationToken);
        var items = new List<PlaylistItem>();
        if (type == "track") items.Add(ToItem(root));
        else
        {
            var page = root.Child("tracks");
            if (page.ValueKind == JsonValueKind.Undefined) page = root.Child("items");
            var visited = new HashSet<string>();
            while (page.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in page.Child("items").Array())
                {
                    var track = type == "playlist" ? entry.Child("track") : entry;
                    if (track.ValueKind == JsonValueKind.Undefined) track = entry.Child("item");
                    if (track.ValueKind != JsonValueKind.Object || track.Text("type", "track") != "track") continue;
                    var item = ToItem(track);
                    if (type == "album") item = item with { ExternalAlbum = root.Text("name"), ArtUrl = root.Child("images").Array().FirstOrDefault().Text("url") };
                    items.Add(item);
                    if (items.Count > InputValidation.MaxItems) throw new InvalidDataException("Playlist pagination limit reached.");
                }
                var next = page.Text("next");
                if (string.IsNullOrEmpty(next)) break;
                if (!visited.Add(next) || items.Count >= 20000 || visited.Count >= 400) throw new InvalidDataException("Playlist pagination limit reached.");
                if (!ProviderUrls.IsHost(next, "api.spotify.com")) throw new InvalidDataException("Unexpected Spotify pagination host.");
                page = await api.GetAsync(next, token, cancellationToken);
            }
        }
        InputValidation.Items(items);
        return new(root.Text("name", "Spotify playlist"), "Spotify", input, items, "Metadata only. Matched files play locally; unmatched tracks open in Spotify. Previews are available only when supplied by Spotify.");
    }
    private static PlaylistItem ToItem(JsonElement t)
    {
        var preview = t.Text("preview_url");
        return new() { ExternalTitle = t.Text("name"), ExternalArtist = string.Join(", ", t.Child("artists").Array().Select(a => a.Text("name"))), ExternalAlbum = t.Child("album").Text("name"), DurationMs = t.Number("duration_ms"), ArtUrl = t.Child("album").Child("images").Array().FirstOrDefault().Text("url"), ExternalUrl = t.Child("external_urls").Text("spotify"), PreviewUrl = string.IsNullOrWhiteSpace(preview) ? null : preview, MatchStatus = string.IsNullOrWhiteSpace(preview) ? MatchStatus.Link : MatchStatus.Preview };
    }
}
public sealed class YouTubeImporter(ProviderApi api, IProviderCredentials credentials) : IPlaylistImporter
{
    public bool CanHandle(string input) => ProviderUrls.IsHost(input, "youtube.com", "www.youtube.com", "music.youtube.com");
    public async Task<ResolvedPlaylist> ResolveAsync(string input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.YouTubeApiKey)) throw new ImportConfigurationException("Add a YouTube Data API v3 key in Settings. Text and local playlist imports need no credentials.");
        var list = ProviderUrls.Query(new Uri(input), "list") ?? throw new InvalidDataException("Use a YouTube playlist URL containing list=.");
        var key = Uri.EscapeDataString(credentials.YouTubeApiKey); var id = Uri.EscapeDataString(list);
        var metadata = await api.GetAsync($"https://www.googleapis.com/youtube/v3/playlists?part=snippet&id={id}&key={key}", null, cancellationToken);
        var name = metadata.Child("items").Array().FirstOrDefault().Child("snippet").Text("title", "YouTube playlist");
        var items = new List<PlaylistItem>(); string pageToken = "";
        var visited = new HashSet<string>();
        do
        {
            var root = await api.GetAsync($"https://www.googleapis.com/youtube/v3/playlistItems?part=snippet&maxResults=50&playlistId={id}&key={key}&pageToken={Uri.EscapeDataString(pageToken)}", null, cancellationToken);
            foreach (var entry in root.Child("items").Array())
            {
                var s = entry.Child("snippet"); var video = s.Child("resourceId").Text("videoId");
                if (video.Length == 0 || s.Text("title") is "Deleted video" or "Private video") continue;
                var title = s.Text("title"); var artist = s.Text("videoOwnerChannelTitle").Replace(" - Topic", "");
                var split = title.Split(" - ", 2); if (split.Length == 2) { artist = split[0]; title = split[1]; }
                items.Add(new() { ExternalTitle = title, ExternalArtist = artist, ExternalUrl = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(video), ArtUrl = s.Child("thumbnails").Child("medium").Text("url") });
            }
            InputValidation.Items(items);
            pageToken = root.Text("nextPageToken");
            if (pageToken.Length > 0 && (!visited.Add(pageToken) || items.Count >= 20000 || visited.Count >= 400)) throw new InvalidDataException("Playlist pagination limit reached.");
        } while (pageToken.Length > 0);
        return new(name, "YouTube", input, items, "Reference playlist only. YouTube audio is never downloaded or played inside Resonance.");
    }
}
public sealed class AppleMusicImporter(ProviderApi api, IProviderCredentials credentials) : IPlaylistImporter
{
    public bool CanHandle(string input) => ProviderUrls.IsHost(input, "music.apple.com");
    public async Task<ResolvedPlaylist> ResolveAsync(string input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.AppleDeveloperToken)) throw new ImportConfigurationException("Add an Apple Music developer token in Settings. A MusicKit-capable Apple developer account is required.");
        var parts = new Uri(input).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts.Contains("playlist")) throw new InvalidDataException("Use an Apple Music catalog playlist URL.");
        var root = await api.GetAsync($"https://api.music.apple.com/v1/catalog/{Uri.EscapeDataString(parts[0])}/playlists/{Uri.EscapeDataString(parts[^1])}", credentials.AppleDeveloperToken, cancellationToken);
        var playlist = root.Child("data").Array().FirstOrDefault(); var page = playlist.Child("relationships").Child("tracks");
        var items = new List<PlaylistItem>(); var visited = new HashSet<string>();
        while (page.ValueKind == JsonValueKind.Object)
        {
            foreach (var t in page.Child("data").Array())
            {
                var a = t.Child("attributes");
                items.Add(new() { ExternalTitle = a.Text("name"), ExternalArtist = a.Text("artistName"), ExternalAlbum = a.Text("albumName"), DurationMs = a.Number("durationInMillis"), ExternalUrl = a.Text("url"), ArtUrl = a.Child("artwork").Text("url").Replace("{w}", "300").Replace("{h}", "300") });
            }
            InputValidation.Items(items);
            var next = page.Text("next"); if (next.Length == 0) break;
            if (!visited.Add(next) || items.Count >= 20000 || visited.Count >= 400) throw new InvalidDataException("Playlist pagination limit reached.");
            var nextUrl = new Uri(new Uri("https://api.music.apple.com"), next).AbsoluteUri;
            if (!ProviderUrls.IsHost(nextUrl, "api.music.apple.com")) throw new InvalidDataException("Unexpected Apple pagination host.");
            page = await api.GetAsync(nextUrl, credentials.AppleDeveloperToken, cancellationToken);
        }
        return new(playlist.Child("attributes").Text("name", "Apple Music playlist"), "Apple Music", input, items);
    }
}
public sealed class SoundCloudImporter(ProviderApi api, IProviderCredentials credentials) : IPlaylistImporter
{
    public bool CanHandle(string input) => ProviderUrls.IsHost(input, "soundcloud.com", "www.soundcloud.com");
    public async Task<ResolvedPlaylist> ResolveAsync(string input, CancellationToken cancellationToken)
    {
        if (!new Uri(input).AbsolutePath.Contains("/sets/")) throw new InvalidDataException("Use a SoundCloud /sets/ playlist URL.");
        if (string.IsNullOrWhiteSpace(credentials.SoundCloudAccessToken))
            return new("SoundCloud reference", "SoundCloud", input, [new() { ExternalTitle = "Open playlist on SoundCloud", ExternalUrl = input }], "No SoundCloud API token configured. This is a single source link, not a resolved track list. Add an authorized token and refresh to resolve metadata.");
        var root = await api.GetAsync("https://api.soundcloud.com/resolve?url=" + Uri.EscapeDataString(input), credentials.SoundCloudAccessToken, cancellationToken, "OAuth");
        var items = new List<PlaylistItem>();
        foreach (var entry in root.Child("tracks").Array())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.Count >= InputValidation.MaxItems) throw new InvalidDataException("Playlist pagination limit reached.");
            var t = entry;
            if (t.Text("title").Length == 0 && t.Number("id") != 0) t = await api.GetAsync("https://api.soundcloud.com/tracks/" + t.Number("id"), credentials.SoundCloudAccessToken, cancellationToken, "OAuth");
            items.Add(new() { ExternalTitle = t.Text("title"), ExternalArtist = t.Child("user").Text("username"), DurationMs = t.Number("duration"), ExternalUrl = t.Text("permalink_url"), ArtUrl = t.Text("artwork_url") });
        }
        InputValidation.Items(items);
        return new(root.Text("title", "SoundCloud playlist"), "SoundCloud", input, items, "Metadata only; no SoundCloud streams are downloaded.");
    }
}
