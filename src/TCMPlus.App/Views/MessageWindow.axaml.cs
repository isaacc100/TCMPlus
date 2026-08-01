using Avalonia.Interactivity;

namespace TCMPlus.App.Views;

public partial class MessageWindow : ResponsiveDialogWindow
{
    private readonly bool _confirmation;

    public MessageWindow(
        string heading,
        string message,
        bool confirmation = false,
        string confirmationText = "Delete",
        string cancelText = "Cancel")
    {
        InitializeComponent();
        Title = heading;
        _confirmation = confirmation;
        Heading.Text = heading;
        Message.Text = message;
        CancelButton.IsVisible = confirmation;
        CancelButton.Content = cancelText;
        ConfirmButton.Content = confirmation ? confirmationText : "OK";
    }

    public MessageWindow() : this("", "")
    {
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(_confirmation);
}
