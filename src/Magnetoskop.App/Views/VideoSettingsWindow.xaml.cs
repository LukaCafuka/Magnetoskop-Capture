using System.Windows;
using Magnetoskop.App.ViewModels;

namespace Magnetoskop.App.Views;

public partial class VideoSettingsWindow : Window
{
    public VideoSettingsWindow(VideoSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Setting DialogResult closes a ShowDialog()-opened window automatically.
        viewModel.RequestClose += (_, _) => DialogResult = viewModel.DialogAccepted;
    }
}
