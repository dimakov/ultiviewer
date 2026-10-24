using System.Globalization;
using System.Windows;
using Ultiviewer.App.ViewModels;

namespace Ultiviewer.App.Views;

/// <summary>A reminder card for one task: open it, tick it off, snooze it for 10 minutes, or dismiss it.</summary>
public partial class ReminderWindow : Window
{
    private readonly TaskEntry _task;
    private readonly MainViewModel _vm;

    public ReminderWindow(TaskEntry task, MainViewModel vm)
    {
        InitializeComponent();
        _task = task;
        _vm = vm;
        When.Text = task.Info.Reminder?.ToString("HH:mm", CultureInfo.CurrentCulture) + (task.DueLabel is { } due ? " · due " + due.ToLowerInvariant() : "");
        Description.Text = task.Description;
        Where.Text = $"{task.Title} · {task.Location}";
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        (Application.Current.MainWindow as MainWindow)?.ShowFromTray();
        _vm.OpenTask(_task);
        Close();
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        _task.Done = true; // ticks it in the page (a recurring task gets its next occurrence)
        Close();
    }

    private void OnSnooze(object sender, RoutedEventArgs e)
    {
        _vm.SnoozeTask(_task, 10);
        Close();
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Close();
}
