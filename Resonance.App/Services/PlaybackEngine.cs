using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Resonance.Core;
using System.Windows;
using System.Windows.Media;

namespace Resonance.App.Services;

public sealed class PlaybackEngine : IPlaybackEngine
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private WaveStream? reader;
    private WasapiOut? output;
    private RampSampleProvider? ramp;
    private MediaPlayer? fallback;
    private float volume = .7f;
    private int generation;
    private bool disposed;
    public event EventHandler? Ended;
    public event EventHandler<string>? Failed;
    public bool IsPlaying { get; private set; }
    public float Volume
    {
        get => volume;
        set { volume = Math.Clamp(value, 0, 1); if (IsPlaying) ramp?.SetGain(volume, 30); if (fallback is not null) fallback.Volume = volume; }
    }
    public TimeSpan Position
    {
        get => reader?.CurrentTime ?? fallback?.Position ?? TimeSpan.Zero;
        set
        {
            var position = TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 0, Math.Max(0, Duration.TotalSeconds - .02)));
            if (reader is not null) reader.CurrentTime = position;
            else if (fallback is not null) fallback.Position = position;
        }
    }
    public TimeSpan Duration => reader?.TotalTime ?? (fallback?.NaturalDuration.HasTimeSpan == true ? fallback.NaturalDuration.TimeSpan : TimeSpan.Zero);
    public async Task OpenAsync(string path)
    {
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsPlaying) await FadeDownAsync();
            CloseCurrent(); var version = generation;
            if (!File.Exists(path) && (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || uri.Scheme != "https")) throw new FileNotFoundException("This audio file is no longer available. Reconnect its drive or rescan your library.", path);
            try
            {
                reader = await Task.Run<WaveStream>(() => {
                    if (Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".flac" or ".aiff" or ".aif") return new AudioFileReader(path);
                    return new MediaFoundationReader(path);
                });
                ramp = new RampSampleProvider(reader.ToSampleProvider());
                output = new WasapiOut(AudioClientShareMode.Shared, false, 60);
                output.Init(ramp.ToWaveProvider());
                output.PlaybackStopped += (_, e) => Application.Current.Dispatcher.BeginInvoke(() => {
                    if (generation != version || disposed) return;
                    if (e.Exception is not null) { IsPlaying = false; Failed?.Invoke(this, "Audio output failed: " + e.Exception.Message); }
                    else if (IsPlaying) { IsPlaying = false; Ended?.Invoke(this, EventArgs.Empty); }
                });
            }
            catch (Exception nativeError)
            {
                output?.Dispose(); output = null; reader?.Dispose(); reader = null; ramp = null;
                fallback = new MediaPlayer();
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fallback.MediaOpened += (_, _) => opened.TrySetResult();
                fallback.MediaFailed += (_, e) => {
                    if (!opened.Task.IsCompleted) opened.TrySetException(new InvalidOperationException("Neither Windows decoder could open this format. " + e.ErrorException.Message, nativeError));
                    else { IsPlaying = false; Failed?.Invoke(this, "Windows media playback failed: " + e.ErrorException.Message); }
                };
                fallback.MediaEnded += (_, _) => { IsPlaying = false; Ended?.Invoke(this, EventArgs.Empty); };
                fallback.Volume = 0; fallback.Open(new Uri(path, UriKind.Absolute));
                try { await opened.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch { CloseCurrent(); throw; }
            }
        }
        finally { gate.Release(); }
    }
    public async Task PlayAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (reader is null && fallback is null) return;
            if (Position >= Duration - TimeSpan.FromMilliseconds(80)) Position = TimeSpan.Zero;
            IsPlaying = true;
            if (output is not null) { ramp!.Reset(); output.Play(); ramp.SetGain(volume, 120); }
            else if (fallback is not null) { fallback.Volume = 0; fallback.Play(); await FadeFallbackAsync(true); }
        }
        finally { gate.Release(); }
    }
    public async Task PauseAsync()
    {
        await gate.WaitAsync();
        try { if (!IsPlaying) return; await FadeDownAsync(); output?.Pause(); fallback?.Pause(); IsPlaying = false; }
        finally { gate.Release(); }
    }
    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try { if (IsPlaying) await FadeDownAsync(); IsPlaying = false; output?.Pause(); fallback?.Pause(); Position = TimeSpan.Zero; }
        finally { gate.Release(); }
    }
    private async Task FadeDownAsync()
    {
        if (ramp is not null) { ramp.SetGain(0, 120); await Task.Delay(145); }
        else if (fallback is not null) await FadeFallbackAsync(false);
    }
    private async Task FadeFallbackAsync(bool up)
    {
        var media = fallback; if (media is null) return;
        var start = media.Volume;
        for (var i = 1; i <= 8; i++) { await Task.Delay(15); media.Volume = up ? volume * i / 8d : start * (1 - i / 8d); }
    }
    private void CloseCurrent()
    {
        generation++; IsPlaying = false;
        output?.Dispose(); output = null; reader?.Dispose(); reader = null; ramp = null;
        fallback?.Close(); fallback = null;
    }
    public void Dispose() { if (disposed) return; disposed = true; CloseCurrent(); }
}

/// <summary>Per-sample volume ramps avoid clicks without relying on dispatcher timer precision.</summary>
internal sealed class RampSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly object sync = new();
    private float current;
    private float target;
    private int left;
    public WaveFormat WaveFormat => source.WaveFormat;
    public void Reset() { lock (sync) { current = target = 0; left = 0; } }
    public void SetGain(float gain, int milliseconds)
    {
        lock (sync) { target = gain; left = Math.Max(1, WaveFormat.SampleRate * WaveFormat.Channels * milliseconds / 1000); }
    }
    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        lock (sync)
        {
            for (var i = 0; i < read; i++)
            {
                if (left > 0) { current += (target - current) / left; left--; }
                buffer[offset + i] *= current;
            }
        }
        return read;
    }
}
