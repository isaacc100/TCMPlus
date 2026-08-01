using Avalonia.Interactivity;
using TCMPlus.Infrastructure.Sessions;

namespace TCMPlus.App.Views;

public partial class RecentSessionsWindow : ResponsiveDialogWindow
{
    public RecentSessionsWindow(EncryptedSessionStore store)
    {
        InitializeComponent();
        SessionsView.UseStore(store);
        SessionsView.OpenRequested += (_, request) => Close(request);
        Opened += async (_, _) => await SessionsView.ActivateAsync();
    }

    public RecentSessionsWindow() : this(new EncryptedSessionStore())
    {
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
