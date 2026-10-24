using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Ultiviewer.App.ViewModels;
using Ultiviewer.App.Views;

namespace Ultiviewer.App.Services;

/// <summary>
/// Shows a reminder card when an open task's <c>⏰ date time</c> arrives (tasks in notebooks, as the To-do pane
/// lists them). Reminders older than a day are not shown; shown ones are remembered in the session. Started by
/// the app at startup only, never by tools that host the window.
/// </summary>
public sealed class ReminderService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly List<ReminderWindow> _open = [];
    private bool _summarized;

    public ReminderService(MainViewModel vm)
    {
        _vm = vm;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Check(), Dispatcher.CurrentDispatcher);
        vm.TasksRefreshed += Check;
    }

    public void Start()
    {
        _timer.Start();
        _vm.RefreshSummaries(); // loads the tasks; TasksRefreshed then checks
    }

    private void Check()
    {
        var now = DateTime.Now;
        var shown = _vm.Session.ShownReminders;
        shown.RemoveAll(key => ReminderTime(key) is { } at && at < now.AddDays(-2));
        foreach (var task in _vm.Tasks.Where(t => !t.Done && t.Info.Reminder is { } at && at <= now && at > now.AddDays(-1)).ToList())
        {
            var key = $"{task.Path}|{task.Info.Reminder!.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}|{task.Description}";
            if (shown.Contains(key))
            {
                continue;
            }

            shown.Add(key);
            Show(task);
        }

        if (!_summarized && _vm.Tasks.Count > 0)
        {
            _summarized = true;
            var overdue = _vm.Tasks.Count(t => !t.Done && t.GroupRank == 0);
            var today = _vm.Tasks.Count(t => !t.Done && t.GroupRank == 1);
            if (overdue + today > 0)
            {
                _vm.StatusMessage = $"To-do: {today} due today{(overdue > 0 ? $", {overdue} overdue" : "")} (see the To-do pane)";
            }
        }
    }

    private void Show(TaskEntry task)
    {
        var window = new ReminderWindow(task, _vm);
        window.Closed += (_, _) =>
        {
            _open.Remove(window);
            Arrange();
        };
        _open.Add(window);
        window.Show();
        Arrange();
    }

    /// <summary>Stacks the cards upwards from the bottom-right corner of the work area.</summary>
    private void Arrange()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom - 12;
        foreach (var window in _open)
        {
            window.UpdateLayout();
            window.Left = area.Right - window.ActualWidth - 12;
            window.Top = bottom - window.ActualHeight;
            bottom = window.Top - 8;
        }
    }

    private static DateTime? ReminderTime(string key) =>
        key.Split('|') is [_, var time, ..] && DateTime.TryParseExact(time, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    public void Dispose()
    {
        _timer.Stop();
        _vm.TasksRefreshed -= Check;
    }
}
