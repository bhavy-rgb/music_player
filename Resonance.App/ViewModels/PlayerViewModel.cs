using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resonance.App.Services;
using Resonance.Core;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace Resonance.App.ViewModels;

public partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackEngine engine;
    private readonly ILibraryStore store;
    private readonly SavedSettings settings;
    private readonly Notifications notifications;
    private readonly PlaybackQueue queue = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool opened, closing;
    public ObservableCollection<Track> Queue { get; } = [];
    [ObservableProperty] private Track? current;
    [ObservableProperty] private Track? selectedQueueTrack;
    [ObservableProperty] private bool isPlaying;
    [ObservableProperty] private double duration;
    [ObservableProperty] private double position;
    [ObservableProperty] private double volume = .7;
    [ObservableProperty] private PlaybackMode mode;
    public string PositionLabel => TimeSpan.FromSeconds(Position).ToString(@"h\:mm\:ss");
    public string DurationLabel => TimeSpan.FromSeconds(Duration).ToString(@"h\:mm\:ss");

    public PlayerViewModel(IPlaybackEngine engine, ILibraryStore store, SavedSettings settings, Notifications notifications)
    {
        this.engine = engine; this.store = store; this.settings = settings; this.notifications = notifications;
        engine.Ended += OnEnded; engine.Failed += OnFailed;
        timer.Tick += Tick; timer.Start();
    }
    public async Task RestoreAsync()
    {
        var session = await settings.ReadAsync("player", new PlayerSession([], -1, PlaybackMode.Sequential, .7),
            s => s.Queue is not null && s.Queue.Length <= InputValidation.MaxItems && s.Queue.All(id => id > 0)
                && s.Index >= -1 && s.Index < Math.Max(1, s.Queue.Length) && Enum.IsDefined(s.Mode)
                && double.IsFinite(s.Volume) && s.Volume is >= 0 and <= 1);
        var tracks = (await store.GetTracksAsync()).ToDictionary(t => t.Id);
        var restored = session.Queue.Where(tracks.ContainsKey).Select(id => tracks[id]).ToArray();
        var currentId = session.Index >= 0 && session.Index < session.Queue.Length ? session.Queue[session.Index] : 0;
        var index = restored.Length == session.Queue.Length ? session.Index : System.Array.FindIndex(restored, t => t.Id == currentId);
        queue.Replace(restored, Math.Max(0, index)); queue.SetMode(session.Mode);
        Mode = session.Mode; Volume = session.Volume; RefreshQueue();
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (closing) return;
        await gate.WaitAsync();
        try { if (!closing) await notifications.RunAsync(action); }
        finally { IsPlaying = engine.IsPlaying; gate.Release(); }
    }
    public Task PlayTracksAsync(IReadOnlyList<Track> tracks, Track selected) => RunAsync(async () => {
        if (tracks.Count > InputValidation.MaxItems) throw new InvalidDataException("Queue is limited to 20,000 tracks.");
        queue.Replace(tracks, Math.Max(0, tracks.ToList().IndexOf(selected))); RefreshQueue();
        await OpenCurrentAsync(); await SaveAsync();
    });
    private async Task OpenCurrentAsync()
    {
        opened = false;
        Current = queue.Current;
        if (Current is null) { await engine.StopAsync(); return; }
        await engine.StopAsync();
        var indexed = (await store.GetTracksAsync()).FirstOrDefault(t => t.Id == Current.Id && t.FilePath == Current.FilePath)
            ?? throw new InvalidDataException("This track is no longer indexed. Rescan your library.");
        InputValidation.AudioPath(indexed.FilePath);
        await engine.OpenAsync(indexed.FilePath); opened = true; await engine.PlayAsync();
    }
    [RelayCommand] private Task PlayPauseAsync() => RunAsync(async () => {
        if (engine.IsPlaying) await engine.PauseAsync();
        else if (opened) await engine.PlayAsync();
        else await OpenCurrentAsync();
    });
    [RelayCommand] private Task StopAsync() => RunAsync(engine.StopAsync);
    [RelayCommand] private Task NextAsync() => AdvanceAsync(false);
    private Task AdvanceAsync(bool automatic) => RunAsync(async () => {
        if (queue.Next(automatic) is null) await engine.StopAsync();
        else await OpenCurrentAsync();
        await SaveAsync();
    });
    [RelayCommand] private Task PreviousAsync() => RunAsync(async () => {
        if (queue.Previous() is not null) await OpenCurrentAsync();
        await SaveAsync();
    });
    [RelayCommand] private Task PlayQueuedAsync() => RunAsync(async () => {
        if (SelectedQueueTrack is null) return;
        queue.Select(Queue.IndexOf(SelectedQueueTrack)); await OpenCurrentAsync(); await SaveAsync();
    });
    [RelayCommand] private Task CycleModeAsync() => RunAsync(async () => {
        Mode = (PlaybackMode)(((int)Mode + 1) % 4); queue.SetMode(Mode); await SaveAsync();
    });
    partial void OnVolumeChanged(double value)
    {
        if (double.IsFinite(value)) engine.Volume = (float)Math.Clamp(value, 0, 1);
    }
    partial void OnPositionChanged(double value) => OnPropertyChanged(nameof(PositionLabel));
    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(DurationLabel));
    public void Seek(double seconds)
    {
        if (!opened || !double.IsFinite(seconds) || gate.CurrentCount == 0) return;
        try { engine.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Duration)); }
        catch (Exception e) { notifications.Show("Could not seek: " + e.Message); }
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (gate.CurrentCount == 0 || closing) return;
        Duration = Math.Max(0, engine.Duration.TotalSeconds); Position = Math.Max(0, engine.Position.TotalSeconds);
        IsPlaying = engine.IsPlaying;
    }
    private void RefreshQueue()
    {
        Queue.Clear(); foreach (var track in queue.Items) Queue.Add(track);
        Current = queue.Current;
    }
    private async void OnEnded(object? sender, EventArgs e)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => AdvanceAsync(true)).Task.Unwrap();
    }
    private void OnFailed(object? sender, string error) => Application.Current.Dispatcher.Invoke(() => {
        IsPlaying = false; opened = false; notifications.Show(error);
    });
    private Task SaveAsync() => settings.WriteAsync("player", new PlayerSession(queue.Items.Select(t => t.Id).ToArray(), queue.Index, queue.Mode, Volume));
    public async Task ShutdownAsync()
    {
        closing = true; timer.Stop(); await gate.WaitAsync();
        try { await engine.StopAsync(); await SaveAsync(); }
        finally { gate.Release(); }
    }
    public void Dispose() { timer.Stop(); timer.Tick -= Tick; engine.Ended -= OnEnded; engine.Failed -= OnFailed; }
}
