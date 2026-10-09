using Resonance.App.Services;
using Resonance.App.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Resonance.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private readonly SavedSettings settings;
    private bool closing, closed, seeking;
    public MainWindow(MainViewModel vm, SavedSettings settings)
    {
        this.vm = vm; this.settings = settings; InitializeComponent(); DataContext = vm;
    }
    public async Task RestoreGeometryAsync()
    {
        var area = SystemParameters.WorkArea;
        var geometry = await settings.ReadAsync("window", new WindowGeometry(area.Left, area.Top, Width, Height, false),
            g => new[] { g.Left, g.Top, g.Width, g.Height }.All(double.IsFinite)
                && g.Width >= MinWidth && g.Width <= 20000 && g.Height >= MinHeight && g.Height <= 20000);
        Width = Math.Min(geometry.Width, area.Width); Height = Math.Min(geometry.Height, area.Height);
        // Restore onto the primary work area if monitors or DPI have changed.
        Left = Math.Clamp(geometry.Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(geometry.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        if (geometry.Maximized) WindowState = WindowState.Maximized;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        e.Cancel = true; if (closing) return; closing = true; IsEnabled = false;
        try
        {
            var rect = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            await vm.ShutdownAsync();
            await settings.WriteAsync("window", new WindowGeometry(rect.Left, rect.Top, rect.Width, rect.Height, WindowState == WindowState.Maximized));
        }
        catch (Exception)
        { MessageBox.Show("Some session settings could not be saved. Your library and audio files are untouched.", "Resonance", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            await ((App)Application.Current).StopHostAsync();
            closed = true; Close(); Application.Current.Shutdown();
        }
    }
    private void BeginSeek()
    {
        if (seeking) return;
        var value = SeekSlider.Value; BindingOperations.ClearBinding(SeekSlider, Slider.ValueProperty);
        SeekSlider.Value = value; seeking = true;
    }
    private void EndSeek()
    {
        if (!seeking) return;
        seeking = false; vm.Player.Seek(SeekSlider.Value);
        SeekSlider.SetBinding(Slider.ValueProperty, new Binding(nameof(PlayerViewModel.Position)) { Mode = BindingMode.OneWay });
    }
    private void SeekStarted(object sender, MouseButtonEventArgs e) => BeginSeek();
    private void SeekFinished(object sender, MouseButtonEventArgs e) => EndSeek();
    private void SeekCaptureLost(object sender, MouseEventArgs e) => EndSeek();
    private void SeekKeyDown(object sender, KeyEventArgs e) { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) BeginSeek(); }
    private void SeekKeyUp(object sender, KeyEventArgs e) => EndSeek();
}
