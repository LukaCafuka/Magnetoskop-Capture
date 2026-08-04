using System.Windows;
using System.Windows.Input;
using Magnetoskop.App.ViewModels;

namespace Magnetoskop.App.Views;

public partial class WatchWindow : Window
{
    private readonly MainViewModel _viewModel;

    public WatchWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void JogShuttleWheel_Released(object sender, MouseEventArgs e)
    {
        _ = _viewModel.ReleaseJogShuttleWheelAsync();
    }
}
