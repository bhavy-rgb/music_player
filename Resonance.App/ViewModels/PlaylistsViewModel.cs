using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resonance.App.Services;
using Resonance.Core;
using System.Collections.ObjectModel;
using System.Windows;

namespace Resonance.App.ViewModels;

public partial class PlaylistsViewModel(ILibraryStore store, PlayerViewModel player, Notifications notifications) : ObservableObject
{
    public ObservableCollection<Playlist> Playlists { get; } = [];
    [ObservableProperty] private Playlist? selected;
    [ObservableProperty] private PlaylistItem? selectedItem;
    public async Task ReloadAsync()
    {
        var id = Selected?.Id;
        var playlists = await store.GetPlaylistsAsync();
        Playlists.Clear(); foreach (var playlist in playlists) Playlists.Add(playlist);
        Selected = Playlists.FirstOrDefault(p => p.Id == id) ?? Playlists.FirstOrDefault();
    }
    [RelayCommand] private Task PlayAsync() => notifications.RunAsync(async () => {
        if (Selected is null) return;
        var library = (await store.GetTracksAsync()).ToDictionary(t => t.Id);
        var tracks = Selected.Items.Where(i => i.TrackId.HasValue && library.ContainsKey(i.TrackId.Value))
            .Select(i => library[i.TrackId!.Value]).ToArray();
        if (tracks.Length == 0) { notifications.Show("This playlist has no local matches. Import or rescan your music first."); return; }
        await player.PlayTracksAsync(tracks, tracks[0]);
    });
    [RelayCommand] private Task DeleteAsync() => notifications.RunAsync(async () => {
        if (Selected is null || MessageBox.Show("Delete this playlist? Your audio files will not be changed.", "Resonance", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await store.DeletePlaylistAsync(Selected.Id); await ReloadAsync();
    });
    [RelayCommand] private void OpenLink()
    {
        try { ExternalLinks.Open(SelectedItem?.ExternalUrl); }
        catch (Exception e) { notifications.Show(e.Message); }
    }
}
