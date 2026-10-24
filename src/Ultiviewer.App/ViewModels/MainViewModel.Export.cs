using System.IO;
using CommunityToolkit.Mvvm.Input;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.App.ViewModels;

/// <summary>What to export or print: a page, or every page of a section in tree order.</summary>
public sealed record ExportRequest(string Title, IReadOnlyList<ExportPage> Pages, bool IsSection, bool PrintWhenReady);

/// <summary>HTML / PDF export and printing of pages and sections (the window renders and saves).</summary>
public partial class MainViewModel
{
    public event Action<ExportRequest>? ExportRequested;

    /// <summary>Export the given page or section (from the tree), or the active tab.</summary>
    [RelayCommand]
    private void ExportDocument(TreeNodeViewModel? node) => RequestExport(node, print: false);

    [RelayCommand]
    private void Print(TreeNodeViewModel? node) => RequestExport(node, print: true);

    private void RequestExport(TreeNodeViewModel? node, bool print)
    {
        ExportRequest? request = node switch
        {
            { Kind: NodeKind.Section or NodeKind.Notebook } container => new ExportRequest(
                container.Name,
                [.. container.DescendantsAndSelf().Where(n => n.Kind == NodeKind.Page).Select(PageForExport).OfType<ExportPage>()],
                IsSection: true,
                print),
            { Kind: NodeKind.Page } page when PageForExport(page) is { } single => new ExportRequest(page.Name, [single], false, print),
            null when ActiveTab is { IsMarkdown: true } tab => new ExportRequest(
                tab.Title, [new ExportPage(tab.Title, tab.Document.Text, tab.Path is null ? null : Path.GetDirectoryName(tab.Path))], false, print),
            _ => null,
        };

        if (request is null || request.Pages.Count == 0)
        {
            StatusMessage = "Open a Markdown page (or choose a page or section in the tree) to export or print it.";
            return;
        }

        ExportRequested?.Invoke(request);
    }

    /// <summary>A page's current text: the open tab's (unsaved edits included) or the file's.</summary>
    private ExportPage? PageForExport(TreeNodeViewModel node)
    {
        try
        {
            var text = OpenTabs.FirstOrDefault(t => t.IsAt(node.Path))?.Document.Text ?? TextDocumentFile.Load(node.Path).Text;
            return new ExportPage(node.Name, text, Path.GetDirectoryName(node.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
