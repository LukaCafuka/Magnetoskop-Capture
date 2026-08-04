using System.Windows;
using System.Windows.Controls;
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
        PreviewKeyDown += OnPreviewKeyDown;
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

    private void JogShuttleWheel_Released(object sender, MouseEventArgs e)
    {
        _ = _viewModel.ReleaseJogShuttleWheelAsync();
    }
}
