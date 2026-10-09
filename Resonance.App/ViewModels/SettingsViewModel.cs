using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resonance.App.Services;
using Resonance.Core;

namespace Resonance.App.ViewModels;

public partial class SettingsViewModel(Credentials credentials, ThemeService themes, SavedSettings settings,
    LibraryViewModel library, Notifications notifications) : ObservableObject
{
    public LibraryViewModel Library { get; } = library;
    public string[] Themes { get; } = ["System", "Light", "Dark"];
    [ObservableProperty] private string theme = "System";
    [ObservableProperty] private bool reducedMotion;
    [ObservableProperty] private string spotifyClientId = "";
    [ObservableProperty] private string credentialStatus = "";
    private bool loading;
    private readonly SemaphoreSlim appearanceGate = new(1, 1);
    public async Task InitializeAsync()
    {
        loading = true;
        var appearance = await settings.ReadAsync("appearance", new Appearance(), a => Themes.Contains(a.Theme));
        Theme = appearance.Theme; ReducedMotion = appearance.ReducedMotion;
        SpotifyClientId = credentials.Configuration.SpotifyClientId;
        loading = false; ApplyTheme(); UpdateCredentialStatus();
        if (credentials.LoadWarning is not null) notifications.Show(credentials.LoadWarning);
    }
    private void ApplyTheme() => themes.ApplyPreference(Theme, ReducedMotion);
    partial void OnThemeChanged(string value) { if (!loading) ApplyTheme(); }
    partial void OnReducedMotionChanged(bool value) { if (!loading) ApplyTheme(); }
    public async Task SaveAppearanceAsync()
    {
        await appearanceGate.WaitAsync();
        try { await settings.WriteAsync("appearance", new Appearance(Theme, ReducedMotion)); }
        finally { appearanceGate.Release(); }
    }
    [RelayCommand] private Task SaveAppearanceCommandAsync() => notifications.RunAsync(SaveAppearanceAsync);
    public Task SaveCredentialsAsync(string youtube, string apple, string soundcloud, bool clear) => notifications.RunAsync(() => {
        InputValidation.Text(SpotifyClientId, 256);
        foreach (var value in new[] { youtube, apple, soundcloud }) InputValidation.Text(value, 8192);
        credentials.Configuration.SpotifyClientId = SpotifyClientId.Trim();
        if (clear || youtube.Length > 0) credentials.State.YouTubeApiKey = youtube.Trim();
        if (clear || apple.Length > 0) credentials.State.AppleDeveloperToken = apple.Trim();
        if (clear || soundcloud.Length > 0) credentials.State.SoundCloudAccessToken = soundcloud.Trim();
        credentials.Save(); UpdateCredentialStatus(); notifications.Show("Provider settings saved locally.");
        return Task.CompletedTask;
    });
    [RelayCommand(IncludeCancelCommand = true)] private Task ConnectSpotifyAsync(CancellationToken ct) => notifications.RunAsync(async () => {
        InputValidation.Text(SpotifyClientId, 256);
        credentials.Configuration.SpotifyClientId = SpotifyClientId.Trim(); credentials.Save();
        await credentials.ConnectSpotifyAsync(ct); UpdateCredentialStatus();
    });
    [RelayCommand] private void DisconnectSpotify()
    {
        try { credentials.DisconnectSpotify(); UpdateCredentialStatus(); }
        catch (Exception e) { notifications.Show(e.Message); }
    }
    private void UpdateCredentialStatus() => CredentialStatus = $"Spotify: {(credentials.SpotifyConnected ? "connected" : "not connected")} · YouTube: {Present(credentials.YouTubeApiKey)} · Apple: {Present(credentials.AppleDeveloperToken)} · SoundCloud: {Present(credentials.SoundCloudAccessToken)}";
    private static string Present(string value) => value.Length > 0 ? "configured" : "not configured";
}
