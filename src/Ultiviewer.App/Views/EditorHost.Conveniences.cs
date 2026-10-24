using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Ultiviewer.App.Services;
using Ultiviewer.App.ViewModels;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.App.Views;

/// <summary>
/// OneNote-like conveniences in Markdown documents: paste screenshots, drop files as attachments, continue
/// lists on Enter, and complete <c>[[page titles]]</c>.
/// </summary>
public partial class EditorHost
{
    private CompletionWindow? _completion;

    private void InitializeConveniences()
    {
        CommandManager.AddPreviewExecutedHandler(Editor.TextArea, OnPreviewExecuted);
        CommandManager.AddPreviewCanExecuteHandler(Editor.TextArea, OnPreviewCanExecute);
        Editor.TextArea.PreviewKeyDown += OnTextAreaPreviewKeyDown;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.PreviewDragOver += OnEditorDragOver;
        Editor.PreviewDrop += OnEditorDrop;
    }

    // ---- Paste images and files ----

    /// <summary>AvalonEdit only allows Paste when the clipboard has text; images and files paste too.</summary>
    private void OnPreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || _current is not { IsMarkdown: true })
        {
            return;
        }

        try
        {
            if (!Clipboard.ContainsText() && (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()))
            {
                e.CanExecute = true;
                e.Handled = true;
            }
        }
        catch (ExternalException)
        {
        }
    }

    private void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || _current is not { IsMarkdown: true })
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsText())
            {
                e.Handled = PasteTable(Clipboard.GetText()); // otherwise an ordinary text paste
                return;
            }

            if (Clipboard.ContainsFileDropList())
            {
                e.Handled = AttachFiles(Clipboard.GetFileDropList().Cast<string>().ToList(), Editor.CaretOffset);
            }
            else if (Clipboard.ContainsImage())
            {
                e.Handled = PasteImage();
            }
        }
        catch (ExternalException ex)
        {
            ErrorLog.Write(ex, "Reading the clipboard");
        }
    }

    private bool PasteImage()
    {
        if (AssetsFolder() is not { } folder || _current?.Path is not { } page)
        {
            return false;
        }

        var path = PageAssets.UniquePath(folder, PageAssets.ScreenshotName(DateTime.Now));
        try
        {
            Directory.CreateDirectory(folder);
            if (Clipboard.GetData("PNG") is MemoryStream png)
            {
                File.WriteAllBytes(path, png.ToArray());
            }
            else if (Clipboard.GetImage() is { } image)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(path);
                encoder.Save(stream);
            }
            else
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            ErrorLog.Write(ex, "Saving a pasted image");
            _vm!.StatusMessage = $"Couldn't save the pasted image: {ex.Message}";
            return false;
        }

        InsertText(PageAssets.MarkdownLink(page, path));
        _vm!.StatusMessage = $"Pasted image saved as {Path.GetFileName(folder)}\\{Path.GetFileName(path)}";
        return true;
    }

    /// <summary>Copies files into the page's attachment folder and inserts links at <paramref name="offset"/>.</summary>
    private bool AttachFiles(IReadOnlyList<string> files, int offset)
    {
        var sources = files.Where(File.Exists).ToList();
        if (sources.Count == 0 || AssetsFolder() is not { } folder || _current?.Path is not { } page)
        {
            return false;
        }

        var links = new List<string>();
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var source in sources)
            {
                var target = PageAssets.UniquePath(folder, Path.GetFileName(source));
                File.Copy(source, target);
                links.Add(PageAssets.MarkdownLink(page, target));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _vm!.StatusMessage = $"Couldn't attach the file: {ex.Message}";
            return false;
        }

        Editor.Select(Math.Clamp(offset, 0, Editor.Document.TextLength), 0);
        InsertText(string.Join(NewLineAt(Editor.TextArea.Caret.Line), links));
        _vm!.StatusMessage = $"Attached {links.Count} file{(links.Count == 1 ? "" : "s")} to the page";
        return true;
    }

    /// <summary>The attachment folder of the current page; untitled documents must be saved first.</summary>
    private string? AssetsFolder()
    {
        if (_current is not { Path: { } path })
        {
            _vm!.StatusMessage = "Save the page first, then paste or drop files into it.";
            return null;
        }

        return PageAssets.FolderFor(path);
    }

    private void OnEditorDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnEditorDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        e.Handled = true;
        if (_current is { IsMarkdown: true, Path: not null } && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            var offset = position is { } p ? Editor.Document.GetOffset(p.Location) : Editor.CaretOffset;
            AttachFiles(files, offset);
        }
        else
        {
            // Other documents (or Shift+drop): open the files instead.
            foreach (var file in files.Where(File.Exists))
            {
                _vm?.OpenDocument(file, preview: false);
            }
        }
    }

    // ---- Lists ----

    private void OnTextAreaPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Tab or Key.Enter && _completion is null && _current is { IsMarkdown: true } && HandleTableKey(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab && _completion is null && _current is { IsMarkdown: true } && HandleListIndent(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || _completion is not null
            || _current is not { IsMarkdown: true } || Editor.SelectionLength > 0)
        {
            return;
        }

        var document = Editor.Document;
        var line = document.GetLineByOffset(Editor.CaretOffset);
        if (Editor.CaretOffset != line.EndOffset)
        {
            return;
        }

        var action = ListContinuation.OnEnter(document.GetText(line));
        if (action.EndsList)
        {
            document.Replace(line.Offset, line.Length, "");
            e.Handled = true;
        }
        else if (action.NextPrefix is { } prefix)
        {
            InsertText(NewLineAt(line.LineNumber) + prefix);
            e.Handled = true;
        }
    }

    // ---- [[ completion ----

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != "[" || _current is not { IsMarkdown: true } || _vm is null)
        {
            return;
        }

        var caret = Editor.CaretOffset;
        if (caret < 2 || Editor.Document.GetCharAt(caret - 2) != '[')
        {
            return;
        }

        var pages = _vm.AllPagePaths()
            .Where(p => !string.Equals(p, _current.Path, StringComparison.OrdinalIgnoreCase))
            .Select(p => new WikiCompletion(Path.GetFileNameWithoutExtension(p), _vm.NotebookOf(p)?.Name + " › " + Path.GetFileName(Path.GetDirectoryName(p))))
            .DistinctBy(c => c.Text, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.Text, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (pages.Count == 0)
        {
            return;
        }

        _completion = new CompletionWindow(Editor.TextArea) { Width = 380, CloseWhenCaretAtBeginning = true };
        foreach (var page in pages)
        {
            _completion.CompletionList.CompletionData.Add(page);
        }

        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
    }

    private sealed class WikiCompletion(string title, string location) : ICompletionData
    {
        public System.Windows.Media.ImageSource? Image => null;

        public string Text => title;

        public object Content => title;

        public object Description => location;

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            var document = textArea.Document;
            var end = completionSegment.EndOffset;
            var hasClosing = end + 1 < document.TextLength && document.GetText(end, 2) == "]]";
            document.Replace(completionSegment, title + (hasClosing ? "" : "]]"));
        }
    }
}
