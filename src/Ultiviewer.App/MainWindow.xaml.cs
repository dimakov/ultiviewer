using System.ComponentModel;
using System.Windows;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        RestorePlacement();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        SavePlacement();
        _vm.SaveState();
    }

    private void RestorePlacement()
    {
        var placement = _vm.Session.Window;
        Width = Math.Max(MinWidth, placement.Width);
        Height = Math.Max(MinHeight, placement.Height);
        var onScreen = !double.IsNaN(placement.Left) && !double.IsNaN(placement.Top)
            && placement.Left >= SystemParameters.VirtualScreenLeft - 50
            && placement.Top >= SystemParameters.VirtualScreenTop - 50
            && placement.Left + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && placement.Top + 100 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (onScreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = placement.Left;
            Top = placement.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        TreeColumn.Width = new GridLength(Math.Clamp(_vm.Session.TreeWidth, 160, 800));
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var placement = _vm.Session.Window;
        placement.Left = bounds.Left;
        placement.Top = bounds.Top;
        placement.Width = bounds.Width;
        placement.Height = bounds.Height;
        placement.Maximized = WindowState == WindowState.Maximized;
        _vm.Session.TreeWidth = TreeColumn.ActualWidth;
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
