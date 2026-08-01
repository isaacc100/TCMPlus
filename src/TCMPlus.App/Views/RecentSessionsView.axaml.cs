using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TCMPlus.Domain.Models;
using TCMPlus.Infrastructure.Sessions;

namespace TCMPlus.App.Views;

public partial class RecentSessionsView : UserControl
{
    private EncryptedSessionStore _store;

    public RecentSessionsView() : this(new EncryptedSessionStore())
    {
    }

    public RecentSessionsView(EncryptedSessionStore store)
    {
        _store = store;
        InitializeComponent();
    }

    public event EventHandler<SessionOpenRequest>? OpenRequested;
    private SessionCatalogEntry? Selected => SessionsList.SelectedItem as SessionCatalogEntry;

    public async Task ActivateAsync()
    {
        await ReloadAsync();
        SessionsList.Focus();
    }

    public void ShowError(string message) => ValidationMessage.Text = message;

    public void UseStore(EncryptedSessionStore store) => _store = store;

    private async Task ReloadAsync()
    {
        try
        {
            SessionsList.ItemsSource = await _store.GetRecentAsync();
            SessionDetails.Text = "Select a shift to load, rename, export, or delete it.";
            ValidationMessage.Text = string.Empty;
        }
        catch (Exception exception)
        {
            SessionsList.ItemsSource = Array.Empty<SessionCatalogEntry>();
            SessionDetails.Text = "Saved shifts could not be read.";
            ValidationMessage.Text = exception.Message;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Selected is { } item)
        {
            SessionDetails.Text = $"Created {item.CreatedAt.LocalDateTime:g} · Last opened {item.LastOpenedAt.LocalDateTime:g}";
        }
    }

    private void OnLoad(object? sender, RoutedEventArgs e)
    {
        if (Selected is null || string.IsNullOrWhiteSpace(PasswordInput.Text))
        {
            ValidationMessage.Text = "Select a shift and enter its session password.";
            return;
        }

        ValidationMessage.Text = "Opening shift…";
        OpenRequested?.Invoke(this, new SessionOpenRequest(Selected, PasswordInput.Text));
    }

    private async void OnRename(object? sender, RoutedEventArgs e)
    {
        if (Selected is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var name = await new TextEntryWindow("Rename shift", "Shift name", Selected.ShiftName).ShowDialog<string?>(owner);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await _store.RenameAsync(Selected, name);
            await ReloadAsync();
        }
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (Selected is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var ok = await new MessageWindow("Delete shift", $"Delete {Selected.ShiftName}? This cannot be undone.", true)
            .ShowDialog<bool>(owner);
        if (ok)
        {
            await _store.DeleteAsync(Selected);
            await ReloadAsync();
        }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (Selected is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider) return;
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"{Selected.ShiftName}.tcm",
            DefaultExtension = "tcm",
            FileTypeChoices = [new FilePickerFileType("TCM session") { Patterns = ["*.tcm"] }]
        });
        if (file is not null) await _store.ExportAsync(Selected, file.Path.LocalPath);
    }
}

public sealed record SessionOpenRequest(SessionCatalogEntry Entry, string Password);
