using System.Windows;
using Microsoft.Win32;
using Ultiviewer.App.Views;
using Ultiviewer.Core;

namespace Ultiviewer.App.Services;

public interface IDialogService
{
    void ShowError(string message);

    void ShowInfo(string message);

    bool Confirm(string message, bool warning = false);

    /// <summary>Yes / No / Cancel. Returns null for Cancel.</summary>
    bool? AskYesNoCancel(string message);

    string? PickFolder(string title, string? initialFolder = null);

    string? PickFile(string title, string filter = "All files|*.*");

    string? PickSaveFile(string title, string fileName, string filter);

    /// <summary>Asks for a line of text. <paramref name="validate"/> returns an error message or null.</summary>
    string? Prompt(string title, string label, string initial = "", Func<string, string?>? validate = null);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } window ? window : null;

    public void ShowError(string message) =>
        Show(message, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string message) =>
        Show(message, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool Confirm(string message, bool warning = false) =>
        Show(message, MessageBoxButton.OKCancel, warning ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.OK;

    public bool? AskYesNoCancel(string message) =>
        Show(message, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public string? PickFolder(string title, string? initialFolder = null)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (initialFolder is not null)
        {
            System.IO.Directory.CreateDirectory(initialFolder);
            dialog.InitialDirectory = initialFolder;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? PickFile(string title, string filter = "All files|*.*")
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string fileName, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = fileName, Filter = filter };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? Prompt(string title, string label, string initial = "", Func<string, string?>? validate = null)
    {
        var dialog = new InputDialog(title, label, initial, validate) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private static MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, AppInfo.Name, buttons, image)
            : MessageBox.Show(message, AppInfo.Name, buttons, image);
}
