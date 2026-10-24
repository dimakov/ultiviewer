using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Ultiviewer.App.Editor;
using Ultiviewer.App.Services;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Views;

/// <summary>
/// Page history: versions on the left, the changes since the selected version (or the version itself) on the
/// right. Modeless, so text can be copied back into the page while it stays open.
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly HistoryViewModel _vm;
    private readonly DiffRenderer _renderer = new();
    private readonly DispatcherTimer _refresh;

    public HistoryWindow(HistoryViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        var palette = ThemeService.Instance.Palette;
        Viewer.Background = new SolidColorBrush(palette.Background);
        Viewer.Foreground = new SolidColorBrush(palette.Foreground);
        Viewer.TextArea.SelectionBrush = new SolidColorBrush(palette.Selection);
        Viewer.TextArea.SelectionForeground = null;
        Viewer.TextArea.TextView.BackgroundRenderers.Add(_renderer);

        // The comparison follows the page while it is being edited.
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _refresh!.Stop();
            _vm.Refresh();
            _vm.UpdateView();
        }, Dispatcher);
        _refresh.Stop();
        vm.Tab.Document.TextChanged += OnTabTextChanged;
        vm.ViewChanged += ShowView;
        Closed += (_, _) =>
        {
            vm.Tab.Document.TextChanged -= OnTabTextChanged;
            vm.ViewChanged -= ShowView;
            _refresh.Stop();
        };
        ShowView();
    }

    public DocumentTabViewModel Tab => _vm.Tab;

    private void OnTabTextChanged(object? sender, EventArgs e)
    {
        _refresh.Stop();
        _refresh.Start();
    }

    private void ShowView()
    {
        _renderer.Kinds = _vm.LineKinds;
        Viewer.Text = _vm.ViewText;
        Viewer.ScrollToVerticalOffset(0);
        if (_vm.FirstChangeLine > 3)
        {
            Dispatcher.BeginInvoke(() => Viewer.ScrollToLine(_vm.FirstChangeLine + 1), DispatcherPriority.Loaded);
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = Viewer.SelectionLength > 0 ? Viewer.SelectedText : _vm.SelectedText();
        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Another program holds the clipboard; the user can try again.
            }
        }
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } version)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Replace the text of \"{_vm.Tab.Title}\" with the version from {version.Label}?\n\nThe current text is kept in the history, and Ctrl+Z in the page undoes the restore.",
            "Restore version", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK)
        {
            _vm.Restore();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
