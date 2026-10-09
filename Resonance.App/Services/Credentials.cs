using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Resonance.Core;

namespace Resonance.App.Services;

public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Resonance");
    public static string Database => Path.Combine(Root, "library.db");
    public static string Art => Path.Combine(Root, "art");
}
public sealed class CredentialState
{
    public string YouTubeApiKey { get; set; } = "";
    public string AppleDeveloperToken { get; set; } = "";
    public string SoundCloudAccessToken { get; set; } = "";
    public string SpotifyAccessToken { get; set; } = "";
    public string SpotifyRefreshToken { get; set; } = "";
    public DateTimeOffset SpotifyExpiresAt { get; set; }
}
public sealed class PublicConfiguration
{
    public string SpotifyClientId { get; set; } = "";
}
public sealed class Credentials : IProviderCredentials
{
    public const string RedirectUri = "http://127.0.0.1:43821/callback/";
    private readonly HttpClient http;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    public CredentialState State { get; private set; } = new();
    public PublicConfiguration Configuration { get; private set; } = new();
    public string? LoadWarning { get; private set; }
    private string SecretPath => Path.Combine(AppPaths.Root, "credentials.dpapi");
    private string ConfigPath => Path.Combine(AppPaths.Root, "config.json");
    public Credentials(HttpClient http)
    {
        this.http = http; Directory.CreateDirectory(AppPaths.Root);
        try
        {
            if (File.Exists(ConfigPath)) Configuration = JsonSerializer.Deserialize<PublicConfiguration>(File.ReadAllText(ConfigPath)) ?? new();
            if (File.Exists(SecretPath))
            {
                var decrypted = ProtectedData.Unprotect(File.ReadAllBytes(SecretPath), null, DataProtectionScope.CurrentUser);
                try { State = JsonSerializer.Deserialize<CredentialState>(decrypted) ?? new(); }
                finally { CryptographicOperations.ZeroMemory(decrypted); }
            }
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException) { LoadWarning = "Saved credentials could not be opened. Re-enter them in Settings on this Windows account."; }
    }
    public string YouTubeApiKey => State.YouTubeApiKey;
    public string AppleDeveloperToken => State.AppleDeveloperToken;
    public string SoundCloudAccessToken => State.SoundCloudAccessToken;
    public bool SpotifyConnected => !string.IsNullOrEmpty(State.SpotifyRefreshToken);
    public void Save()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Configuration, new JsonSerializerOptions { WriteIndented = true }));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(State);
        try
        {
            File.WriteAllBytes(SecretPath + ".tmp", ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            File.Move(SecretPath + ".tmp", SecretPath, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void DisconnectSpotify() { State.SpotifyAccessToken = ""; State.SpotifyRefreshToken = ""; State.SpotifyExpiresAt = default; Save(); }
    public async Task<string> GetSpotifyTokenAsync(CancellationToken ct)
    {
        await tokenLock.WaitAsync(ct);
        try
        {
            if (State.SpotifyExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1) && State.SpotifyAccessToken.Length > 0) return State.SpotifyAccessToken;
            if (Configuration.SpotifyClientId.Length == 0 || State.SpotifyRefreshToken.Length == 0) throw new ImportConfigurationException("Set your Spotify client ID in Settings, then click Connect Spotify. No client secret is needed.");
            await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = State.SpotifyRefreshToken, ["client_id"] = Configuration.SpotifyClientId }, ct);
            return State.SpotifyAccessToken;
        }
        finally { tokenLock.Release(); }
    }
    public async Task ConnectSpotifyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Configuration.SpotifyClientId)) throw new ImportConfigurationException("Enter and save your Spotify client ID first.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var listener = new HttpListener(); listener.Prefixes.Add(RedirectUri); listener.Start();
        var url = "https://accounts.spotify.com/authorize?response_type=code&client_id=" + Uri.EscapeDataString(Configuration.SpotifyClientId) + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) + "&scope=playlist-read-private%20playlist-read-collaborative&code_challenge_method=S256&code_challenge=" + challenge + "&state=" + state;
        ExternalLinks.Open(url);
        try
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            var query = context.Request.QueryString;
            var valid = query["state"] == state && !string.IsNullOrEmpty(query["code"]) && query["error"] is null;
            var body = Encoding.UTF8.GetBytes(valid ? "Authorization received. You can close this tab and return to Resonance." : "Authorization was not accepted. Return to Resonance and try again.");
            context.Response.ContentType = "text/plain; charset=utf-8"; context.Response.StatusCode = valid ? 200 : 400; context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, ct); context.Response.Close();
            if (!valid) throw new InvalidOperationException("Spotify authorization was denied or its security state did not match.");
            await tokenLock.WaitAsync(ct);
            try { await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["code"] = query["code"]!, ["redirect_uri"] = RedirectUri, ["client_id"] = Configuration.SpotifyClientId, ["code_verifier"] = verifier }, ct); }
            finally { tokenLock.Release(); }
        }
        finally { listener.Stop(); }
    }
    private async Task ExchangeAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        using var response = await http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(fields), ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Spotify authorization failed ({(int)response.StatusCode}). Check your client ID and redirect URI, then reconnect.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var root = json.RootElement;
        State.SpotifyAccessToken = root.GetProperty("access_token").GetString()!;
        if (root.TryGetProperty("refresh_token", out var refresh)) State.SpotifyRefreshToken = refresh.GetString()!;
        State.SpotifyExpiresAt = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()); Save();
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
public static class ExternalLinks
{
    public static void Open(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "spotify")) throw new InvalidOperationException("This entry has no safe external link.");
        // Official Spotify deep link; Windows asks for a handler if Spotify is not installed.
        if (uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var index = System.Array.FindIndex(parts, p => p is "track" or "album" or "playlist");
            if (index >= 0 && index + 1 < parts.Length) value = $"spotify:{parts[index]}:{parts[index + 1]}";
        }
        Process.Start(new ProcessStartInfo(value!) { UseShellExecute = true });
    }
}
