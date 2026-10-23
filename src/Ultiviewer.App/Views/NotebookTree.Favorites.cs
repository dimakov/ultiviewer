using System.Windows.Controls;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Views;

public partial class NotebookTree
{
    partial void AddPageExtras(ContextMenu menu, TreeNodeViewModel node)
    {
        var vm = Vm;
        var item = new MenuItem { Header = vm.IsFavorite(node.Path) ? "Remove from favorites" : "Add to favorites" };
        item.Click += (_, _) => vm.ToggleFavorite(node.Path);
        menu.Items.Add(item);
    }
}
