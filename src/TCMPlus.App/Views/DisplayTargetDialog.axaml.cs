using System.Collections.ObjectModel;
using Avalonia.Interactivity;

namespace TCMPlus.App.Views;

public partial class DisplayTargetDialog : ResponsiveDialogWindow
{
    public DisplayTargetDialog(IEnumerable<DisplayTargetOption> options, string? preferredId)
    {
        InitializeComponent();
        Options = new ObservableCollection<DisplayTargetOption>(options);
        SelectedOption = Options.FirstOrDefault(option => option.Id == preferredId) ?? Options.FirstOrDefault();
        DataContext = this;
        Opened += (_, _) => DestinationBox.Focus();
    }

    public DisplayTargetDialog() : this([], null) { }

    public ObservableCollection<DisplayTargetOption> Options { get; }
    public DisplayTargetOption? SelectedOption { get; set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnOpen(object? sender, RoutedEventArgs e) => Close(SelectedOption?.Id);
}

public sealed record DisplayTargetOption(string Id, string Label, string Detail);
