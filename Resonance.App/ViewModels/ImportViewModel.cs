using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resonance.Core;
using System.Collections.ObjectModel;

namespace Resonance.App.ViewModels;

public partial class ImportRow(PlaylistItem item) : ObservableObject
{
    public PlaylistItem Item { get; } = item;
    [ObservableProperty] private bool included = true;
    public string Title => Item.ExternalTitle;
    public string Artist => Item.ExternalArtist;
    public string Status => Item.MatchStatus == MatchStatus.Local ? $"LOCAL · {Item.Confidence:P0}" : Item.PreviewUrl is not null ? "PREVIEW / LINK" : Item.LocalPath is not null ? "MISSING FILE" : "LINK";
}
public partial class ImportViewModel(IEnumerable<IPlaylistImporter> importers, ILibraryStore store, ILibraryScanner scanner) : ObservableObject
{
    [ObservableProperty] private string input = "";
    [ObservableProperty] private string name = "Imported playlist";
    [ObservableProperty] private string message = "Paste a playlist URL or Artist - Title lines. You choose what gets saved.";
    [ObservableProperty] private string provider = "IMPORT";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool resolved;
    public ObservableCollection<ImportRow> Rows { get; } = [];
    public Playlist? Existing { get; set; }
    private ResolvedPlaylist? result;
    public Playlist? SavedPlaylist { get; private set; }
    public event EventHandler? Saved;
    private CancellationTokenSource? cancellation;
    [RelayCommand] private void Cancel() => cancellation?.Cancel();
    [RelayCommand] private void SelectAll() { foreach (var row in Rows) row.Included = true; }
    [RelayCommand] private void SelectNone() { foreach (var row in Rows) row.Included = false; }
    [RelayCommand]
    private async Task ResolveAsync()
    {
        if (Busy) return;
        cancellation?.Dispose(); cancellation = new(); var ct = cancellation.Token;
        Busy = true; Resolved = false; Rows.Clear(); Message = "Resolving metadata…";
        try
        {
            var importer = importers.FirstOrDefault(i => i.CanHandle(Input.Trim())) ?? throw new InvalidDataException("That URL is not supported. Use an official provider URL, a .m3u/.pls link, or paste Artist - Title lines.");
            result = await importer.ResolveAsync(Input, ct);
            Name = Existing?.Name ?? result.Name; Provider = result.Provider.ToUpperInvariant();
            var library = await store.GetTracksAsync();
            // Only read files explicitly named by a local playlist; never scan unrelated directories.
            var paths = result.Items.Where(i => i.LocalPath is not null && File.Exists(i.LocalPath)).Select(i => i.LocalPath!).ToArray();
            int scanErrors = 0;
            if (paths.Length > 0)
            {
                var scan = await scanner.ScanFilesAsync(paths, library, ct); scanErrors = scan.Errors.Count;
                await store.ApplyScanAsync(scan); library = await store.GetTracksAsync();
            }
            Message = "Matching against your local library…";
            var matched = await Task.Run(() => result.Items.Select(item => { ct.ThrowIfCancellationRequested(); return LibraryMatcher.Match(item, library); }).ToArray(), ct);
            foreach (var item in matched) Rows.Add(new(item));
            var summary = $"{Rows.Count} tracks · {matched.Count(i => i.TrackId.HasValue)} local matches";
            if (Existing is not null)
            {
                static string Key(PlaylistItem i) => i.ExternalUrl ?? $"{LibraryMatcher.Normalize(i.ExternalArtist)}|{LibraryMatcher.Normalize(i.ExternalTitle)}";
                var old = Existing.Items.Select(Key).ToHashSet(); var next = matched.Select(Key).ToHashSet();
                summary += $" · {next.Except(old).Count()} added / {old.Except(next).Count()} removed from source";
            }
            Message = summary + ". " + result.Notice + (scanErrors > 0 ? $" {scanErrors} local file(s) could not be read." : "");
            Resolved = true;
        }
        catch (OperationCanceledException) { Message = "Import cancelled. No playlist was saved."; }
        catch (Exception e) { Message = e.Message; }
        finally { Busy = false; }
    }
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Busy || !Resolved || result is null) return;
        if (string.IsNullOrWhiteSpace(Name)) { Message = "Give this playlist a name."; return; }
        if (!Rows.Any(r => r.Included)) { Message = "Select at least one track to save."; return; }
        Busy = true;
        try
        {
            var playlist = new Playlist { Id = Existing?.Id ?? 0, CreatedAt = Existing?.CreatedAt ?? DateTimeOffset.UtcNow, Name = Name.Trim(), SourceProvider = result.Provider, SourceUrl = result.SourceUrl, LastSyncedAt = DateTimeOffset.UtcNow, Items = Rows.Where(r => r.Included).Select(r => r.Item).ToArray() };
            var id = await store.SavePlaylistAsync(playlist); SavedPlaylist = playlist with { Id = id }; Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { Message = e.Message; }
        finally { Busy = false; }
    }
    public void Close() { cancellation?.Cancel(); }
}
