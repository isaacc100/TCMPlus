using Avalonia.Interactivity;

namespace TCMPlus.App.Views;

public partial class TextEntryWindow : ResponsiveDialogWindow
{
    public TextEntryWindow(string title, string label, string value = "")
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        Label.Text = label;
        Input.Text = value;
        Opened += (_, _) => Input.Focus();
    }

    public TextEntryWindow() : this("", "")
    {
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    private void OnSave(object? sender, RoutedEventArgs e) => Close(Input.Text?.Trim());
}
