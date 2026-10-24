using System.IO;
using CommunityToolkit.Mvvm.Input;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Links;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>Preview interaction: view modes, following links, toggling tasks.</summary>
public sealed partial class MainViewModel
{
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp",
        ".scr", ".lnk", ".url", ".hta", ".cpl", ".reg", ".jar", ".appref-ms", ".application",
    };

    /// <summary>Raised after every edit of an open document (the preview re-renders from it).</summary>
    public event Action<DocumentTabViewModel>? DocumentEdited;

    partial void OnDocumentEdited(DocumentTabViewModel tab) => DocumentEdited?.Invoke(tab);

    /// <summary>The active tab's view mode, or null when it isn't Markdown (the toggle is hidden).</summary>
    public ViewMode? ActiveViewMode => FocusedGroup?.ActiveViewMode;

    [RelayCommand]
    private void CycleViewMode()
    {
        if (ActiveTab is { IsMarkdown: true } tab)
        {
            SetTabViewMode(tab, FocusedGroup.ModeOf(tab) switch
            {
                ViewMode.Live => ViewMode.Edit,
                ViewMode.Edit => ViewMode.Split,
                ViewMode.Split => ViewMode.Preview,
                _ => ViewMode.Live,
            });
        }
    }

    [RelayCommand]
    private void SetViewMode(string mode)
    {
        if (Enum.TryParse<ViewMode>(mode, out var value) && ActiveTab is { IsMarkdown: true } tab)
        {
            SetTabViewMode(tab, value);
        }
    }

    /// <summary>
    /// Changes a tab's view mode in the group showing it (the second editor can show a document differently);
    /// the choice also becomes the default for newly opened pages.
    /// </summary>
    public void SetTabViewMode(DocumentTabViewModel tab, ViewMode mode)
    {
        var group = GroupOf(tab) ?? FocusedGroup;
        group.SetMode(tab, mode);
        if (group == Secondary && !Primary.Tabs.Contains(tab))
        {
            tab.ViewMode = mode; // only shown here: the document's own mode follows (session, Unsplit)
        }

        Settings.DefaultMarkdownViewMode = mode;
        OnPropertyChanged(nameof(ActiveViewMode));
        if (mode != ViewMode.Preview)
        {
            EditorFocusRequested?.Invoke();
        }
    }

    public IEnumerable<string> AllPagePaths() =>
        Notebooks.SelectMany(n => n.DescendantsAndSelf()).Where(n => n.Kind == NodeKind.Page).Select(n => n.Path);

    /// <summary>Follows a link clicked in the preview of <paramref name="from"/>.</summary>
    public void OpenLink(DocumentTabViewModel? from, string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return;
        }

        if (href.StartsWith(TagParser.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            SearchFor("#" + MarkdownLinks.Decode(href[TagParser.Scheme.Length..]));
            return;
        }

        if (href.StartsWith(WikiLinkParser.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            OpenWikiLink(from, MarkdownLinks.Decode(href[WikiLinkParser.Scheme.Length..]));
            return;
        }

        if (MarkdownLinks.IsExternal(href))
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            {
                if (uri.Host.EndsWith(".ultiviewer.example", StringComparison.OrdinalIgnoreCase))
                {
                    return; // a resolved preview-internal URL; nothing to open
                }

                ShellService.Open(uri.AbsoluteUri);
            }
            else
            {
                StatusMessage = $"Links of this kind aren't opened from notes: {href}";
            }

            return;
        }

        var (rawPath, suffix) = MarkdownLinks.SplitSuffix(href);
        var baseFolder = from?.Path is { } source ? Path.GetDirectoryName(source) : null;
        if (baseFolder is null)
        {
            StatusMessage = "Save the page first so relative links can be resolved.";
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(baseFolder, MarkdownLinks.Decode(rawPath).Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            StatusMessage = $"Invalid link: {href}";
            return;
        }

        OpenPathFromLink(full, suffix);
    }

    private void OpenPathFromLink(string full, string suffix)
    {
        if (Directory.Exists(full))
        {
            if (SelectPath(full) is null)
            {
                ShellService.RevealInExplorer(full);
            }

            return;
        }

        if (!File.Exists(full))
        {
            StatusMessage = $"Link target not found: {full}";
            return;
        }

        if (ProgramExtensions.Contains(Path.GetExtension(full)))
        {
            if (!_dialogs.Confirm($"\"{Path.GetFileName(full)}\" is a program or script. Run it?", warning: true))
            {
                return;
            }

            ShellService.Open(full);
            return;
        }

        if (TextDocumentFile.Inspect(full) != LoadProblem.None)
        {
            ShellService.Open(full); // images, PDFs, Office files…
            return;
        }

        int? line = null;
        if (suffix.StartsWith('#') && NotebookScanner.IsPageFile(full))
        {
            var text = OpenTabs.FirstOrDefault(t => t.IsAt(full))?.Document.Text ?? TextDocumentFile.Load(full).Text;
            line = MarkdownHeadings.FindLine(text, suffix[1..]);
        }

        OpenDocument(full, preview: false, line: line);
    }

    private void OpenWikiLink(DocumentTabViewModel? from, string target)
    {
        var (title, suffix) = MarkdownLinks.SplitSuffix(target);
        var notebook = from?.Path is { } path ? NotebookOf(path) : null;
        if (WikiLinkResolver.Resolve(title, from?.Path, AllPagePaths(), notebook?.Path) is { } page)
        {
            OpenPathFromLink(page, suffix);
            return;
        }

        if (from?.Path is not { } fromPath || notebook is null)
        {
            StatusMessage = $"No page named \"{title}\".";
            return;
        }

        if (!_dialogs.Confirm($"There is no page named \"{title}\" yet. Create it next to \"{from.Title}\"?"))
        {
            return;
        }

        var parts = title.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var folder = Path.Combine([Path.GetDirectoryName(fromPath)!, .. parts[..^1]]);
        string? created = null;
        if (TryRun(() => created = _ops.CreatePage(folder, parts[^1])))
        {
            RefreshNotebook(notebook);
            OpenDocument(created!, preview: false);
        }
    }

    /// <summary>Flips the checkbox of the task on a (1-based) line, as an undoable edit.</summary>
    public void ToggleTaskAtLine(DocumentTabViewModel tab, int lineNumber)
    {
        var document = tab.Document;
        if (lineNumber < 1 || lineNumber > document.LineCount)
        {
            return;
        }

        var line = document.GetLineByNumber(lineNumber);
        var text = document.GetText(line.Offset, line.Length);

        // Completing adds the done date and, for a recurring task, its next occurrence above it.
        var toggled = string.Join(tab.Format.NewLine, TaskCompletion.Toggle(text, DateOnly.FromDateTime(DateTime.Today)));
        if (toggled != text)
        {
            document.Replace(line.Offset, line.Length, toggled);
        }
    }
}
