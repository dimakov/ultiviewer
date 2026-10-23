using System.Windows.Controls;
using System.Windows.Input;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Search;

namespace Ultiviewer.App.Views;

/// <summary>All #tags across notebooks with page counts; clicking one searches for it.</summary>
public partial class TagsPane : UserControl
{
    public TagsPane()
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

    private void OnTagClicked(object sender, MouseButtonEventArgs e)
    {
        if (TagList.SelectedItem is TagCount tag)
        {
            Vm.SearchFor("#" + tag.Tag);
        }
    }

    private void OnTagKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TagList.SelectedItem is TagCount tag)
        {
            Vm.SearchFor("#" + tag.Tag);
            e.Handled = true;
        }
    }
}
