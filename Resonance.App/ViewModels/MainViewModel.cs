using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resonance.App.Services;
using Resonance.App.Views;
using System.Windows;

namespace Resonance.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public LibraryViewModel Library { get; }
    public PlayerViewModel Player { get; }
    public PlaylistsViewModel Playlists { get; }
    public SettingsViewModel Settings { get; }
    public Notifications Notifications { get; }
    private readonly Func<ImportViewModel> createImport;
    [ObservableProperty] private ObservableObject content;
    public MainViewModel(LibraryViewModel library, PlayerViewModel player, PlaylistsViewModel playlists,
        SettingsViewModel settings, Notifications notifications, Func<ImportViewModel> createImport)
    {
        Library = library; Player = player; Playlists = playlists; Settings = settings;
        Notifications = notifications; this.createImport = createImport; content = library;
    }
    public async Task InitializeAsync()
    {
        await Settings.InitializeAsync(); await Library.InitializeAsync();
        await Playlists.ReloadAsync(); await Player.RestoreAsync();
    }
    [RelayCommand] private void Navigate(string page) => Content = page switch
    {
        "Playlists" => Playlists, "Settings" => Settings, _ => Library
    };
    [RelayCommand] private Task ImportAsync() => ShowImportAsync(false);
    [RelayCommand] private Task ResyncAsync() => ShowImportAsync(true);
    private Task ShowImportAsync(bool resync) => Notifications.RunAsync(async () => {
        var existing = resync ? Playlists.Selected : null;
        if (resync && string.IsNullOrWhiteSpace(existing?.SourceUrl))
        { Notifications.Show("This playlist has no source URL or file to re-sync."); return; }
        var vm = createImport();
        if (existing is not null) { vm.Existing = existing; vm.Input = existing.SourceUrl!; }
        new ImportWindow(vm) { Owner = Application.Current.MainWindow }.ShowDialog();
        await Library.ReloadAsync(); await Playlists.ReloadAsync();
        if (vm.SavedPlaylist is not null)
        {
            Playlists.Selected = Playlists.Playlists.FirstOrDefault(p => p.Id == vm.SavedPlaylist.Id);
            Content = Playlists;
        }
    });
    public async Task ShutdownAsync()
    {
        Settings.ConnectSpotifyCommand.Cancel();
        if (Settings.ConnectSpotifyCommand.ExecutionTask is { } connect) await connect;
        await Library.StopWorkAsync(); await Player.ShutdownAsync(); await Settings.SaveAppearanceAsync();
    }
}
