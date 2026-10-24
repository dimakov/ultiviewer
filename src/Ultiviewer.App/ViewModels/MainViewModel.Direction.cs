using System.Windows.Threading;
using Ultiviewer.Core.Text;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// Text direction of documents: chosen per file (remembered in the session), else the setting, else detected from
/// the text (Hebrew or Arabic lines in the majority read right to left), and re-detected after edits.
/// </summary>
public sealed partial class MainViewModel
{
    private const int DirectionSampleLength = 200_000;

    private Dictionary<string, TextDirectionMode>? _directions;
    private DispatcherTimer? _directionTimer;
    private readonly HashSet<DocumentTabViewModel> _directionPending = [];

    private Dictionary<string, TextDirectionMode> Directions => _directions ??= new(Session.TextDirections, StringComparer.OrdinalIgnoreCase);

    /// <summary>Gives a newly opened tab its stored choice and its effective direction.</summary>
    private void InitializeDirection(DocumentTabViewModel tab)
    {
        tab.DirectionMode = tab.Path is { } path && Directions.TryGetValue(path, out var mode) ? mode : TextDirectionMode.Auto;
        UpdateDirection(tab);
        if (_directionTimer is null)
        {
            _directionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
            {
                _directionTimer!.Stop();
                foreach (var pending in _directionPending)
                {
                    UpdateDirection(pending);
                }

                _directionPending.Clear();
            }, _dispatcher);
            _directionTimer.Stop();
            DocumentEdited += edited =>
            {
                if (edited.DirectionMode == TextDirectionMode.Auto && Settings.TextDirection == TextDirectionMode.Auto)
                {
                    _directionPending.Add(edited);
                    _directionTimer.Stop();
                    _directionTimer.Start();
                }
            };
        }
    }

    /// <summary>Recomputes whether <paramref name="tab"/> shows right to left.</summary>
    public void UpdateDirection(DocumentTabViewModel tab)
    {
        var mode = tab.DirectionMode != TextDirectionMode.Auto ? tab.DirectionMode : Settings.TextDirection;
        tab.IsRightToLeft = mode switch
        {
            TextDirectionMode.RightToLeft => true,
            TextDirectionMode.LeftToRight => false,
            _ => BidiText.Detect(tab.Document.GetText(0, Math.Min(tab.Document.TextLength, DirectionSampleLength))) ?? false,
        };
    }

    /// <summary>Sets a document's direction (Auto goes back to the setting and the text) and remembers it for the file.</summary>
    public void SetDirection(DocumentTabViewModel? tab, TextDirectionMode mode)
    {
        if (tab is null)
        {
            return;
        }

        tab.DirectionMode = mode;
        if (tab.Path is { } path)
        {
            if (mode == TextDirectionMode.Auto)
            {
                Directions.Remove(path);
            }
            else
            {
                Directions[path] = mode;
            }

            Session.TextDirections = new Dictionary<string, TextDirectionMode>(Directions);
        }

        UpdateDirection(tab);
        StatusMessage = mode switch
        {
            TextDirectionMode.RightToLeft => $"{tab.Title}: right to left",
            TextDirectionMode.LeftToRight => $"{tab.Title}: left to right",
            _ => $"{tab.Title}: direction from the text ({(tab.IsRightToLeft ? "right to left" : "left to right")})",
        };
    }

    /// <summary>After the setting changed: every open document follows it again.</summary>
    private void UpdateAllDirections()
    {
        foreach (var tab in OpenTabs)
        {
            UpdateDirection(tab);
        }
    }
}
