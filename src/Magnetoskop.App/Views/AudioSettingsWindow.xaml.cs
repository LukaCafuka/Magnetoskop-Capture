using System.Windows;
using Magnetoskop.App.ViewModels;

namespace Magnetoskop.App.Views;

public partial class AudioSettingsWindow : Window
{
    public AudioSettingsWindow(AudioSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += (_, _) => DialogResult = viewModel.DialogAccepted;
    }
}
