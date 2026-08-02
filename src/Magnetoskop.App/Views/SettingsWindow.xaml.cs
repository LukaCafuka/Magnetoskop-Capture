using System.Windows;
using Magnetoskop.App.ViewModels;

namespace Magnetoskop.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += (_, _) => DialogResult = viewModel.DialogAccepted;
    }
}
