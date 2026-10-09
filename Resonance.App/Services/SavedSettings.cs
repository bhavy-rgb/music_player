using Resonance.Core;
using System.Text.Json;

namespace Resonance.App.Services;

public sealed class SavedSettings(ILibraryStore store, Notifications notifications)
{
    public async Task<T> ReadAsync<T>(string key, T fallback, Func<T, bool> valid)
    {
        var json = await store.GetSettingAsync(key);
        if (json is null) return fallback;
        try
        {
            InputValidation.Text(json, InputValidation.MaxBytes);
            var value = JsonSerializer.Deserialize<T>(json, InputValidation.JsonOptions);
            if (value is null || !valid(value)) throw new InvalidDataException();
            return value;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException)
        {
            notifications.Show($"Saved {key} settings were invalid. Defaults are being used.");
            return fallback;
        }
    }
    public Task WriteAsync<T>(string key, T value) => store.SetSettingAsync(key, JsonSerializer.Serialize(value));
}

public sealed record PlayerSession(long[] Queue, int Index, PlaybackMode Mode, double Volume);
public sealed record Appearance(string Theme = "System", bool ReducedMotion = false);
public sealed record WindowGeometry(double Left, double Top, double Width, double Height, bool Maximized);
