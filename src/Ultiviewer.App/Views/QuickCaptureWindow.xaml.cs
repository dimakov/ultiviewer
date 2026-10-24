using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Capture;

namespace Ultiviewer.App.Views;

/// <summary>
/// A small always-on-top window for jotting a note into today's daily note or the Inbox without leaving what
/// you are doing; opened by the global shortcut (default Win+Alt+N), the tray icon or File → Quick note.
/// </summary>
public partial class QuickCaptureWindow : Window
{
    private readonly MainViewModel _vm;

    public QuickCaptureWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Target.SelectedIndex = vm.Settings.QuickCaptureTarget == CaptureTarget.Inbox ? 1 : 0;
        Target.SelectionChanged += (_, _) => ShowWhere();
        ShowWhere();
        Loaded += (_, _) =>
        {
            if (ShowActivated)
            {
                Activate(); // the global shortcut lets this window come to the front
            }

            Note.Focus();
        };
        try
        {
            ClipboardButton.IsEnabled = Clipboard.ContainsText();
        }
        catch (ExternalException)
        {
            ClipboardButton.IsEnabled = false;
        }
    }

    private CaptureTarget SelectedTarget => Target.SelectedIndex == 1 ? CaptureTarget.Inbox : CaptureTarget.DailyNote;

    private void ShowWhere()
    {
        var path = _vm.CapturePath(SelectedTarget, DateTime.Today);
        Where.Text = path is null ? "Create a notebook first" : Path.GetRelativePath(_vm.CaptureNotebook!.Path, path);
        Where.ToolTip = path;
    }

    private void OnNoteChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        Placeholder.Visibility = Note.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnPasteClipboard(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                Note.Text = Note.Text.Length == 0 ? text : Note.Text.TrimEnd() + Environment.NewLine + text;
                Note.CaretIndex = Note.Text.Length;
                Note.Focus();
            }
        }
        catch (ExternalException)
        {
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            Add(open: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e) => Add(open: false);

    private void OnAddAndOpen(object sender, RoutedEventArgs e) => Add(open: true);

    private void Add(bool open)
    {
        if (Note.Text.Trim().Length == 0)
        {
            if (open)
            {
                _vm.OpenCapturePage(SelectedTarget);
                ShowMainWindow();
            }

            Close();
            return;
        }

        if (_vm.Capture(Note.Text, SelectedTarget) is { } path)
        {
            _vm.Settings.QuickCaptureTarget = SelectedTarget;
            if (open)
            {
                _vm.OpenDocument(path, preview: false, focus: true);
                ShowMainWindow();
            }

            Close();
        }
    }

    private static void ShowMainWindow()
    {
        if (Application.Current.MainWindow is { } main)
        {
            main.Show();
            if (main.WindowState == WindowState.Minimized)
            {
                main.WindowState = WindowState.Normal;
            }

            main.Activate();
        }
    }
}
