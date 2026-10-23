using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Views;

/// <summary>Open tasks across all notebooks (OneNote's tag summary): tick them off or jump to them.</summary>
public partial class TodoPane : UserControl
{
    public TodoPane()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && DataContext is MainViewModel vm)
            {
                vm.RefreshSummaries();
            }
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnTaskTextClicked(object sender, MouseButtonEventArgs e)
    {
        if (NotebookTree.FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is null
            && (sender as FrameworkElement)?.DataContext is TaskEntry task)
        {
            Vm.OpenTask(task);
        }
    }

    private void OnTaskKeyDown(object sender, KeyEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskEntry task)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            Vm.OpenTask(task);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            task.Done = !task.Done;
            e.Handled = true;
        }
    }
}
