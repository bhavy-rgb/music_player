using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Resonance.App.Services;
using Resonance.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace Resonance.App.ViewModels;

public partial class LibraryViewModel(ILibraryStore store, ILibraryScanner scanner, SavedSettings settings,
    PlayerViewModel player, Notifications notifications) : ObservableObject
{
    public ObservableCollection<Track> Tracks { get; } = [];
    public ObservableCollection<string> Folders { get; } = [];
    public ICollectionView View => CollectionViewSource.GetDefaultView(Tracks);
    public string[] Sorts { get; } = ["Artist", "Album", "Title", "Date added"];
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string sort = "Artist";
    [ObservableProperty] private string status = "No tracks yet — add a folder to get started.";
    [ObservableProperty] private Track? selectedTrack;
    [ObservableProperty] private string? selectedFolder;
    [ObservableProperty] private bool busy;
    private CancellationTokenSource? scanCancellation;

    public async Task InitializeAsync()
    {
        var roots = await settings.ReadAsync("folders", System.Array.Empty<string>(), a => a.Length <= 256
            && a.All(p => p is { Length: > 0 and <= 8192 } && Path.IsPathFullyQualified(p)));
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase)) Folders.Add(root);
        View.Filter = item => item is Track t && (t.Title.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || t.Artist.Contains(Search, StringComparison.OrdinalIgnoreCase) || t.Album.Contains(Search, StringComparison.OrdinalIgnoreCase));
        ApplySort();
        await ReloadAsync();
    }
    public async Task ReloadAsync()
    {
        var tracks = await store.GetTracksAsync();
        using (View.DeferRefresh()) { Tracks.Clear(); foreach (var track in tracks) Tracks.Add(track); }
        Status = Tracks.Count == 0 ? "No tracks yet — add a folder to get started." : $"{Tracks.Count} tracks in your library";
    }
    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnSortChanged(string value) => ApplySort();
    private void ApplySort()
    {
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            View.SortDescriptions.Add(new(Sort == "Date added" ? nameof(Track.DateAdded) : Sort,
                Sort == "Date added" ? ListSortDirection.Descending : ListSortDirection.Ascending));
            View.SortDescriptions.Add(new(nameof(Track.Title), ListSortDirection.Ascending));
        }
    }
    [RelayCommand] private Task PlayAsync() => SelectedTrack is null ? Task.CompletedTask
        : player.PlayTracksAsync(View.Cast<Track>().ToArray(), SelectedTrack);
    [RelayCommand] private async Task AddFolderAsync()
    {
        if (Busy) return;
        var picker = new OpenFolderDialog { Title = "Add a music folder", Multiselect = false };
        if (picker.ShowDialog() != true) return;
        await notifications.RunAsync(async () => {
            var path = Path.GetFullPath(picker.FolderName);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Choose a real folder, not a symbolic link.");
            if (Folders.Count >= 256) throw new InvalidDataException("At most 256 library folders are supported.");
            if (!Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                await settings.WriteAsync("folders", Folders.Append(path).ToArray());
                Folders.Add(path);
            }
            await RescanAsync();
        });
    }
    [RelayCommand] private Task RemoveFolderAsync() => notifications.RunAsync(async () => {
        if (Busy || SelectedFolder is null) return;
        var path = SelectedFolder;
        await settings.WriteAsync("folders", Folders.Where(p => p != path).ToArray());
        Folders.Remove(path);
        Status = "Folder removed from future scans. Indexed tracks and audio files were kept.";
    });
    [RelayCommand] private async Task RescanAsync()
    {
        if (Busy) return;
        Busy = true;
        using var cancellation = new CancellationTokenSource(); scanCancellation = cancellation;
        try
        {
            await notifications.RunAsync(async () => {
                if (Folders.Count == 0) { Status = "Add a folder before scanning."; return; }
                var roots = Folders.ToArray();
                Status = "Scanning your music folders…";
                var result = await scanner.ScanAsync(roots, await store.GetTracksAsync(),
                    new Progress<ScanProgress>(p => Status = $"{p.Count} files · {p.CurrentPath}"), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                await store.ApplyScanAsync(result); await ReloadAsync();
                var unavailable = roots.Count(p => !Directory.Exists(p));
                if (result.Errors.Count > 0 || unavailable > 0)
                    notifications.Show($"Scan finished: {result.Errors.Count} unreadable files/folders, {unavailable} unavailable roots. Unreadable entries were kept. " + result.Errors.FirstOrDefault());
            });
        }
        finally { Busy = false; scanCancellation = null; }
    }
    [RelayCommand] private void CancelScan() => scanCancellation?.Cancel();
    public async Task StopWorkAsync()
    {
        CancelScan();
        if (RescanCommand.ExecutionTask is { } rescan) await rescan;
        if (AddFolderCommand.ExecutionTask is { } add) await add;
    }
}
