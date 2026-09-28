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
        JogShuttleDial.Attach(JogShuttleDialSurface, viewModel);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        await _viewModel.InitializeCommand.ExecuteAsync(null);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTextInputFocused()) return;

        if (e.Key is Key.J or Key.K or Key.L)
        {
            if (await _viewModel.HandleJklKeyAsync(e.Key))
            {
                e.Handled = true;
            }

            return;
        }

        if (e.Key is Key.OemComma or Key.OemPeriod)
        {
            if (await _viewModel.HandleFrameStepKeyAsync(e.Key))
            {
                e.Handled = true;
            }
        }
    }

    private bool _ltcCommitting;
    private bool _ctlCommitting;
    private bool _vitcCommitting;

    private void LtcDisplay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.BeginEditLtcCommand.CanExecute(null)) return;
        _viewModel.BeginEditLtcCommand.Execute(null);
        Dispatcher.BeginInvoke(() =>
        {
            LtcEditBox.Focus();
            LtcEditBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
        e.Handled = true;
    }

    private async void LtcEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ltcCommitting = true;
            try
            {
                await _viewModel.CommitGoToLtcCommand.ExecuteAsync(null);
            }
            finally
            {
                _ltcCommitting = false;
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelEditLtcCommand.Execute(null);
        }
    }

    private void LtcEditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_ltcCommitting || !_viewModel.IsEditingLtc) return;
        _viewModel.CancelEditLtcCommand.Execute(null);
    }

    private void VitcDisplay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.BeginEditVitcCommand.CanExecute(null)) return;
        _viewModel.BeginEditVitcCommand.Execute(null);
        Dispatcher.BeginInvoke(() =>
        {
            VitcEditBox.Focus();
            VitcEditBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
        e.Handled = true;
    }

    private async void VitcEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vitcCommitting = true;
            try
            {
                await _viewModel.CommitGoToVitcCommand.ExecuteAsync(null);
            }
            finally
            {
                _vitcCommitting = false;
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelEditVitcCommand.Execute(null);
        }
    }

    private void VitcEditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vitcCommitting || !_viewModel.IsEditingVitc) return;
        _viewModel.CancelEditVitcCommand.Execute(null);
    }

    private void CtlDisplay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.BeginEditCtlCommand.CanExecute(null)) return;
        _viewModel.BeginEditCtlCommand.Execute(null);
        Dispatcher.BeginInvoke(() =>
        {
            CtlEditBox.Focus();
            CtlEditBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
        e.Handled = true;
    }

    private async void CtlEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ctlCommitting = true;
            try
            {
                await _viewModel.CommitGoToCtlCommand.ExecuteAsync(null);
            }
            finally
            {
                _ctlCommitting = false;
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelEditCtlCommand.Execute(null);
        }
    }

    private void CtlEditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_ctlCommitting || !_viewModel.IsEditingCtl) return;
        _viewModel.CancelEditCtlCommand.Execute(null);
    }

    private static bool IsTextInputFocused()
    {
        var focused = Keyboard.FocusedElement;
        return focused is TextBox or PasswordBox
            || focused is ComboBox { IsEditable: true }
            || focused is ComboBoxItem;
    }
}
