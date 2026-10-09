using System.Windows;

namespace Resonance.App.Services;

public sealed class ThemeService
{
    private bool reduceMotion;
    public bool Light { get; private set; }
    public ThemeService() => SystemParameters.StaticPropertyChanged += (_, _) => UpdateMotion();
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
