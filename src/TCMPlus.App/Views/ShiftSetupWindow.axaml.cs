using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace TCMPlus.App.Views;

public partial class ShiftSetupWindow : Window
{
    public ShiftSetupWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ShiftNameInput.Focus();
    }

    public event EventHandler<ShiftSetupDraft>? ShiftStarted;

    private void OnStartShift(object? sender, RoutedEventArgs e)
    {
        var shiftName = ShiftNameInput.Text?.Trim() ?? string.Empty;
        var pin = string.Concat(PinDigitBox1.Text, PinDigitBox2.Text, PinDigitBox3.Text, PinDigitBox4.Text, PinDigitBox5.Text, PinDigitBox6.Text);
        if (string.IsNullOrWhiteSpace(shiftName))
        {
            ValidationMessage.Text = "Enter a shift name.";
            return;
        }

        if (pin.Length != 6 || !pin.All(char.IsAsciiDigit))
        {
            ValidationMessage.Text = "Enter a six-digit PIN.";
            return;
        }

        ShiftStarted?.Invoke(this, new ShiftSetupDraft(shiftName, pin));
    }

    private void OnLoadExistingShift(object? sender, RoutedEventArgs e) =>
        ValidationMessage.Text = "Loading an existing shift will be available in a future update.";

    private void OnPinDigitChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox || string.IsNullOrEmpty(textBox.Text))
        {
            return;
        }

        if (textBox.Text.Length > 1)
        {
            textBox.Text = textBox.Text[^1].ToString();
        }

        NextPinInput(textBox)?.Focus();
    }

    private void OnPinDigitKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Back && string.IsNullOrEmpty(textBox.Text))
        {
            PreviousPinInput(textBox)?.Focus();
        }
        else if (e.Key == Key.Enter)
        {
            OnStartShift(this, e);
        }
    }

    private TextBox? NextPinInput(TextBox input)
    {
        var inputs = PinInputs;
        var index = Array.IndexOf(inputs, input);
        return index >= 0 && index < inputs.Length - 1 ? inputs[index + 1] : null;
    }

    private TextBox? PreviousPinInput(TextBox input)
    {
        var inputs = PinInputs;
        var index = Array.IndexOf(inputs, input);
        return index > 0 ? inputs[index - 1] : null;
    }

    private TextBox[] PinInputs => [PinDigitBox1, PinDigitBox2, PinDigitBox3, PinDigitBox4, PinDigitBox5, PinDigitBox6];
}

public sealed record ShiftSetupDraft(string ShiftName, string Pin);
