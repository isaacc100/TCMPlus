using Avalonia.Controls;
using Avalonia.Interactivity;
namespace TCMPlus.App.Views;
public partial class ProcessConflictWindow : ResponsiveDialogWindow
{
    public ProcessConflictWindow(int count) { InitializeComponent(); MessageText.Text = $"{count} other TCM+ instance{(count == 1 ? " is" : "s are")} already running. They may keep files open."; }
    public ProcessConflictWindow() : this(1) { }
    public event EventHandler<bool>? Resolved;
    public void ShowError(string message) => ErrorText.Text = message;
    private void OnExit(object? sender, RoutedEventArgs e) => Resolved?.Invoke(this, false);
    private void OnTerminate(object? sender, RoutedEventArgs e) => Resolved?.Invoke(this, true);
}
