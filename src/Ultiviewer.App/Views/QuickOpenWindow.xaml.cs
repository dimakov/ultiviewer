using System.Windows;
using System.Windows.Input;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Views;

/// <summary>Ctrl+E: jump to any page by (fuzzy) title. Recent pages come first when the box is empty.</summary>
public partial class QuickOpenWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _closing;

    public QuickOpenWindow(MainViewModel vm, Window owner)
    {
        InitializeComponent();
        _vm = vm;
        Owner = owner;
        var bounds = owner.WindowState == WindowState.Maximized ? new Rect(0, 0, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height) : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
        Left = bounds.Left + (bounds.Width - Width) / 2;
        Top = bounds.Top + 70;
        Loaded += (_, _) =>
        {
            Refresh();
            Query.Focus();
        };
    }

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        Items.ItemsSource = _vm.QuickOpenItems(Query.Text);
        if (Items.Items.Count > 0)
        {
            Items.SelectedIndex = 0;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseOnce();
                e.Handled = true;
                break;
            case Key.Down:
                Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
        }
    }

    private void Move(int step)
    {
        if (Items.Items.Count == 0)
        {
            return;
        }

        Items.SelectedIndex = Math.Clamp(Items.SelectedIndex + step, 0, Items.Items.Count - 1);
        Items.ScrollIntoView(Items.SelectedItem);
    }

    private void OnItemsMouseUp(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Items.SelectedItem is QuickOpenItem item)
        {
            CloseOnce();
            _vm.OpenDocument(item.Path, preview: false, focus: true);
        }
    }

    private void OnDeactivated(object? sender, EventArgs e) => CloseOnce();

    private void CloseOnce()
    {
        if (!_closing)
        {
            _closing = true;
            Close();
        }
    }
}
