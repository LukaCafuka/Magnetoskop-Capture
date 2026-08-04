using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        await _viewModel.InitializeCommand.ExecuteAsync(null);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.J or Key.K or Key.L)) return;
        if (IsTextInputFocused()) return;

        if (await _viewModel.HandleJklKeyAsync(e.Key))
        {
            e.Handled = true;
        }
    }

    private static bool IsTextInputFocused()
    {
        var focused = Keyboard.FocusedElement;
        return focused is TextBox or PasswordBox
            || focused is ComboBox { IsEditable: true }
            || focused is ComboBoxItem;
    }

    private void AudioDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only user-driven changes flip the manual flag: check the combo box has focus.
        if (sender is ComboBox { IsDropDownOpen: true } or ComboBox { IsKeyboardFocusWithin: true })
        {
            _viewModel.AudioManuallySelected = true;
        }
    }

    private void JogShuttleWheel_Released(object sender, MouseEventArgs e)
    {
        _ = _viewModel.ReleaseJogShuttleWheelAsync();
    }
}
