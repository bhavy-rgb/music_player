using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;

namespace Resonance.App.Services;

public partial class Notifications : ObservableObject
{
    [ObservableProperty] private string message = "";
    public bool HasMessage => Message.Length > 0;
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    public void Show(string text) => Application.Current.Dispatcher.Invoke(() => Message = text);
    [RelayCommand] private void Dismiss() => Message = "";
    public async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Show("Operation cancelled."); }
        catch (HttpRequestException) { Show("The provider could not be reached. Check your connection and try again."); }
        catch (Exception e) when (e is not OutOfMemoryException) { Show(e.Message); }
    }
}
