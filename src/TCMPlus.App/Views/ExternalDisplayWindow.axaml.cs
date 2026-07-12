using Avalonia.Controls;
using TCMPlus.App.ViewModels;
using TCMPlus.Domain.Models;

namespace TCMPlus.App.Views;

public partial class ExternalDisplayWindow : Window
{
    public ExternalDisplayWindow(MainViewModel viewModel, ExternalDisplayMode mode)
    {
        InitializeComponent();
        DataContext = viewModel;
        Heading.Text = mode == ExternalDisplayMode.Dashboard ? "TCM+ dashboard" : "TCM+ treatment centre";
        DashboardPanel.IsVisible = mode == ExternalDisplayMode.Dashboard;
        MapPanel.IsVisible = mode == ExternalDisplayMode.Map;
    }

    public ExternalDisplayWindow() : this(null!, ExternalDisplayMode.Dashboard) { }
}
