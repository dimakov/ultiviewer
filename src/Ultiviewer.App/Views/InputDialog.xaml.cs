using System.Windows;
using System.Windows.Controls;

namespace Ultiviewer.App.Views;

public partial class InputDialog : Window
{
    private readonly Func<string, string?>? _validate;

    public InputDialog(string title, string label, string initial, Func<string, string?>? validate)
    {
        InitializeComponent();
        _validate = validate;
        Title = title;
        LabelText.Text = label;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text.Trim();

    private void OnTextChanged(object sender, TextChangedEventArgs e) => ShowError(null);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_validate?.Invoke(Value) is { } error)
        {
            ShowError(error);
            return;
        }

        DialogResult = true;
    }

    private void ShowError(string? error)
    {
        ErrorText.Text = error;
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
