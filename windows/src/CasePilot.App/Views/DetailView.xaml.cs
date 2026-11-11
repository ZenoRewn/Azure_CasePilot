using System.Windows;
using System.Windows.Controls;

namespace CasePilot.App.Views;

public partial class DetailView : UserControl
{
    public DetailView() => InitializeComponent();

    void Close(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is ViewModels.MainVM m) m.CloseDetail();
    }
}
