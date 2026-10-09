using System.Windows;
using Microsoft.Win32;
using System.ComponentModel;

namespace Resonance.App.Services;

public sealed class ThemeService : IDisposable
{
    private bool reduceMotion;
    public bool Light { get; private set; }
    private string preference = "Dark";
    public ThemeService()
    {
        SystemParameters.StaticPropertyChanged += MotionChanged;
        SystemEvents.UserPreferenceChanged += SystemThemeChanged;
    }
    private void MotionChanged(object? sender, PropertyChangedEventArgs e) => UpdateMotion();
    private void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (preference == "System") Application.Current.Dispatcher.BeginInvoke(() => ApplyPreference(preference, reduceMotion));
    }
    public void ApplyPreference(string theme, bool reduced)
    {
        preference = theme;
        bool light = theme == "Light";
        if (theme == "System")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        Apply(light, reduced);
    }
    public void Dispose()
    {
        SystemParameters.StaticPropertyChanged -= MotionChanged;
        SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
    }
    public void Apply(bool light, bool reduced)
    {
        Light = light; reduceMotion = reduced;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary { Source = new Uri($"Styles/{(light ? "Light" : "Dark")}.xaml", UriKind.Relative) };
        if (dictionaries.Count > 0) dictionaries[0] = theme; else dictionaries.Add(theme);
        UpdateMotion();
    }
    private void UpdateMotion() => Application.Current.Dispatcher.Invoke(() => Application.Current.Resources["MotionDuration"] = new Duration(TimeSpan.FromMilliseconds(reduceMotion || !SystemParameters.ClientAreaAnimation ? 0 : 160)));
}
