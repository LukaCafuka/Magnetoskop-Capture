using System.Windows;
using System.Windows.Controls;
using Magnetoskop.App.ViewModels;

namespace Magnetoskop.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _initialized;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        await _viewModel.InitializeCommand.ExecuteAsync(null);
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