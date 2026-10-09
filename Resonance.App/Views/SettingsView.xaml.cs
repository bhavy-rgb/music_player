using Resonance.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
namespace Resonance.App.Views;
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    private async void SaveCredentials(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        await vm.SaveCredentialsAsync(YouTube.Password, Apple.Password, SoundCloud.Password, ClearSecrets.IsChecked == true);
        YouTube.Clear(); Apple.Clear(); SoundCloud.Clear(); ClearSecrets.IsChecked = false;
    }
}
