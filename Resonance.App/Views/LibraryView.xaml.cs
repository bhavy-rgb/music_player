using Resonance.App.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace Resonance.App.Views;
public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();
    private void PlaySelection(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is LibraryViewModel vm && vm.PlayCommand.CanExecute(null)) vm.PlayCommand.Execute(null);
    }
}
