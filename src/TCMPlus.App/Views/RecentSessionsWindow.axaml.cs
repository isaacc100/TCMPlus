using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TCMPlus.Domain.Models;
using TCMPlus.Infrastructure.Sessions;

namespace TCMPlus.App.Views;
public partial class RecentSessionsWindow : ResponsiveDialogWindow
{
    private readonly EncryptedSessionStore _store;
    public RecentSessionsWindow(EncryptedSessionStore store) { _store = store; InitializeComponent(); Opened += async (_, _) => await ReloadAsync(); }
    public RecentSessionsWindow() : this(new EncryptedSessionStore()) { }
    private SessionCatalogEntry? Selected => SessionsList.SelectedItem as SessionCatalogEntry;
    private async Task ReloadAsync()
    {
        try
        {
            SessionsList.ItemsSource = await _store.GetRecentAsync();
            SessionDetails.Text = "Select a shift to load, rename, export, or delete it.";
            ValidationMessage.Text = "";
        }
        catch (Exception exception)
        {
            SessionsList.ItemsSource = Array.Empty<SessionCatalogEntry>();
            SessionDetails.Text = "Saved shifts could not be read.";
            ValidationMessage.Text = exception.Message;
        }
    }
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) { if (Selected is { } item) SessionDetails.Text = $"Created {item.CreatedAt.LocalDateTime:g} · Last opened {item.LastOpenedAt.LocalDateTime:g}"; }
    private void OnLoad(object? sender, RoutedEventArgs e) { if (Selected is null || string.IsNullOrWhiteSpace(PasswordInput.Text)) { ValidationMessage.Text = "Select a shift and enter its session password."; return; } Close(new SessionOpenRequest(Selected, PasswordInput.Text)); }
    private async void OnRename(object? sender, RoutedEventArgs e) { if (Selected is null) return; var name = await new TextEntryWindow("Rename shift", "Shift name", Selected.ShiftName).ShowDialog<string?>(this); if (!string.IsNullOrWhiteSpace(name)) { await _store.RenameAsync(Selected, name); await ReloadAsync(); } }
    private async void OnDelete(object? sender, RoutedEventArgs e) { if (Selected is null) return; var ok = await new MessageWindow("Delete shift", $"Delete {Selected.ShiftName}? This cannot be undone.", true).ShowDialog<bool>(this); if (ok) { await _store.DeleteAsync(Selected); await ReloadAsync(); } }
    private async void OnExport(object? sender, RoutedEventArgs e) { if (Selected is null) return; var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = $"{Selected.ShiftName}.tcm", DefaultExtension = "tcm", FileTypeChoices = [new FilePickerFileType("TCM session") { Patterns = ["*.tcm"] }] }); if (file is not null) await _store.ExportAsync(Selected, file.Path.LocalPath); }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
public sealed record SessionOpenRequest(SessionCatalogEntry Entry, string Password);
