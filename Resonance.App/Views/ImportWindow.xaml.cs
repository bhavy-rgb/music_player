using Microsoft.Win32;
using Resonance.App.ViewModels;
using System.ComponentModel;
using System.Windows;
namespace Resonance.App.Views;
public partial class ImportWindow : Window
{
    private readonly ImportViewModel vm;
    private bool finishing, closed;
    public ImportWindow(ImportViewModel vm)
    {
        this.vm = vm; InitializeComponent(); DataContext = vm;
        vm.Saved += Saved; Closing += ClosingImport;
        Closed += (_, _) => { vm.Saved -= Saved; vm.Close(); };
    }
    private void Saved(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Close);
    private async void ClosingImport(object? sender, CancelEventArgs e)
    {
        if (closed || !vm.Busy) return;
        e.Cancel = true; if (finishing) return; finishing = true; vm.Close();
        if (vm.ResolveCommand.ExecutionTask is { } resolve) await resolve;
        if (vm.SaveCommand.ExecutionTask is { } save) await save;
        closed = true; Close();
    }
    private void CloseDialog(object sender, RoutedEventArgs e) => Close();
    private void ChooseFile(object sender, RoutedEventArgs e)
    {
        if (vm.Busy) return;
        var dialog = new OpenFileDialog { Filter = "Playlists|*.m3u;*.m3u8;*.pls;*.txt", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) vm.Input = dialog.FileName;
    }
}
