using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ICSharpCode.AvalonEdit.Document;
using Ultiviewer.App.Editor;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// One open document: its text (an AvalonEdit <see cref="TextDocument"/> with its own undo history), how it
/// is stored on disk, and the editor view state to restore when its tab is activated again.
/// </summary>
public sealed partial class DocumentTabViewModel : ObservableObject
{
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd", ".mdx",
    };

    public DocumentTabViewModel(string? path, string text, TextFileFormat format, string? untitledName = null)
    {
        Document = new TextDocument(text);
        Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
            {
                IsDirty = !Document.UndoStack.IsOriginalFile;
            }
        };
        Document.Changed += (_, _) => Version++;
        _path = path;
        _format = format;
        UntitledName = untitledName ?? "new";
        _language = HighlightingRegistry.Instance.LanguageForFile(path ?? "untitled.md");
    }

    public TextDocument Document { get; }

    /// <summary>Incremented on every edit; used to tell whether a backup is current.</summary>
    public long Version { get; private set; }

    public string UntitledName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsMarkdown), nameof(ToolTip), nameof(IsUntitled))]
    private string? _path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _isDirty;

    /// <summary>A temporary tab opened by a single click in the tree; the next single click replaces it.</summary>
    [ObservableProperty]
    private bool _isPreviewTab;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _isDeletedOnDisk;

    [ObservableProperty]
    private TextFileFormat _format;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMarkdown))]
    private string _language;

    [ObservableProperty]
    private ViewMode _viewMode = ViewMode.Edit;

    [ObservableProperty]
    private string? _saveError;

    public bool IsUntitled => Path is null;

    public string Title => Path is null
        ? UntitledName
        : IsMarkdown ? System.IO.Path.GetFileNameWithoutExtension(Path) : System.IO.Path.GetFileName(Path);

    public bool IsMarkdown => Path is null
        ? Language == "Markdown"
        : MarkdownExtensions.Contains(System.IO.Path.GetExtension(Path));

    public string ToolTip => (Path ?? UntitledName)
        + (IsDeletedOnDisk ? "\nDeleted on disk" : "")
        + (IsDirty ? "\nUnsaved changes" : "");

    /// <summary>True for Markdown pages inside an open notebook (autosaved like OneNote).</summary>
    public bool IsNotebookPage { get; set; }

    /// <summary>Stamp and content hash of the file as last loaded or saved by us.</summary>
    public FileStamp? KnownStamp { get; set; }

    public string? KnownHash { get; set; }

    /// <summary>Backup file name in the backup folder while the tab has unsaved text.</summary>
    public string? BackupName { get; set; }

    public long BackupVersion { get; set; } = -1;

    // Editor view state, captured when the tab is deactivated.
    public int CaretOffset { get; set; }

    public int SelectionStart { get; set; }

    public int SelectionLength { get; set; }

    public double VerticalOffset { get; set; }

    public double HorizontalOffset { get; set; }

    /// <summary>Line to scroll to when the tab is next shown in the editor (1-based).</summary>
    public int? PendingLine { get; set; }

    public List<int> Bookmarks { get; set; } = [];

    public void MarkSaved(FileStamp stamp, string hash)
    {
        KnownStamp = stamp;
        KnownHash = hash;
        Document.UndoStack.MarkAsOriginalFile();
        IsDirty = false;
        IsDeletedOnDisk = false;
        SaveError = null;
    }

    /// <summary>Replaces the text with what is on disk, keeping the caret close to where it was.</summary>
    public void Reload(LoadedText loaded)
    {
        Document.Text = loaded.Text;
        Document.UndoStack.ClearAll();
        Format = loaded.Format;
        MarkSaved(loaded.Stamp, loaded.Hash);
        CaretOffset = Math.Min(CaretOffset, Document.TextLength);
        SelectionStart = Math.Min(SelectionStart, Document.TextLength);
        SelectionLength = 0;
    }

    public bool IsAt(string path) =>
        Path is not null && string.Equals(System.IO.Path.GetFullPath(Path), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
}
