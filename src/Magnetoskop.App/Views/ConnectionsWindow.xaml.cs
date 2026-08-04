using System.Windows;
using System.Windows.Controls;
using Magnetoskop.App.ViewModels;

namespace Magnetoskop.App.Views;

public partial class ConnectionsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public ConnectionsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void AudioDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only user-driven changes flip the manual flag: check the combo box has focus.
        if (sender is ComboBox { IsDropDownOpen: true } or ComboBox { IsKeyboardFocusWithin: true })
        {
            _viewModel.AudioManuallySelected = true;
        }
    }
}
