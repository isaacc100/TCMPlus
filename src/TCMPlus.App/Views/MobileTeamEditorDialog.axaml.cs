using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TCMPlus.App.ViewModels;

namespace TCMPlus.App.Views;

public partial class MobileTeamEditorDialog : Window
{
    public MobileTeamEditorDialog() : this(null, null)
    {
    }

    public MobileTeamEditorDialog(string? callsign, string? note)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(callsign))
        {
            Heading.Text = "Edit mobile team";
            CallsignInput.Text = callsign;
            NoteInput.Text = note;
        }
        Opened += (_, _) => CallsignInput.Focus();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var callsign = CallsignInput.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(callsign))
        {
            ValidationMessage.Text = "Enter a callsign.";
            return;
        }
        Close(new MobileTeamDraft(callsign, NoteInput.Text?.Trim()));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnCallsignKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { NoteInput.Focus(); e.Handled = true; } }
    private void OnNoteKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { OnSave(sender, e); e.Handled = true; } }
}
