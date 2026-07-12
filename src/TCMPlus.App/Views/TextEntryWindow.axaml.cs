using Avalonia.Controls; using Avalonia.Interactivity;
namespace TCMPlus.App.Views;
public partial class TextEntryWindow : Window { public TextEntryWindow(string title,string label,string value="") { InitializeComponent(); Title=title; Heading.Text=title; Label.Text=label; Input.Text=value; Opened+=(_,_)=>Input.Focus(); } public TextEntryWindow():this("",""){} private void OnCancel(object? s,RoutedEventArgs e)=>Close(); private void OnSave(object? s,RoutedEventArgs e)=>Close(Input.Text?.Trim()); }
