using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Links;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Search;

namespace Ultiviewer.App.ViewModels;

/// <summary>One row of the Links pane: a line of a page (or a link of the active page).</summary>
public sealed record LinkEntry(string Title, string Location, string Path, int Line, string Text, LinkState State = LinkState.Ok, string? Target = null)
{
    public bool IsBroken => State == LinkState.Broken;
}

/// <summary>
/// The Links pane for the active page: pages linking here, the page's own links (broken ones flagged) and
/// unlinked mentions of its title, which one click turns into <c>[[links]]</c>. Candidates for backlinks come from
/// the search index, so large notebooks stay fast; open tabs' unsaved text counts.
/// </summary>
public partial class MainViewModel
{
    private DispatcherTimer? _linksTimer;
    private int _linksVersion;

    public ObservableCollection<LinkEntry> Backlinks { get; } = [];

    public ObservableCollection<LinkEntry> OutgoingLinks { get; } = [];

    public ObservableCollection<LinkEntry> UnlinkedMentions { get; } = [];

    [ObservableProperty]
    private string _linksMessage = "Open a page to see what links to it.";

    private void InitializeLinksPane()
    {
        _linksTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
        {
            _linksTimer!.Stop();
            RefreshLinks();
        }, _dispatcher);
        _linksTimer.Stop();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveTab))
            {
                RefreshLinks();
            }
        };
        DocumentEdited += tab =>
        {
            if (tab == ActiveTab)
            {
                _linksTimer.Stop();
                _linksTimer.Start();
            }
        };
        IndexUpdated += () =>
        {
            _linksTimer.Stop();
            _linksTimer.Start();
        };
    }

    /// <summary>Recomputes the pane for the active page (backlinks and mentions on a background thread).</summary>
    public async void RefreshLinks()
    {
        var version = ++_linksVersion;
        if (ActiveTab is not { IsMarkdown: true, Path: { } path } tab)
        {
            Backlinks.Clear();
            OutgoingLinks.Clear();
            UnlinkedMentions.Clear();
            LinksMessage = "Open a saved Markdown page to see what links to it.";
            return;
        }

        var allPages = AllPagePaths().ToList();
        var roots = allPages.ToDictionary(p => p, p => NotebookOf(p)?.Path, StringComparer.OrdinalIgnoreCase);
        var root = roots.GetValueOrDefault(path);
        var text = tab.Document.Text;
        var buffers = OpenTabs.Where(t => t.Path is not null).ToDictionary(t => t.Path!, t => t.Document.Text, StringComparer.OrdinalIgnoreCase);
        var indexes = _search?.Indexes ?? [];
        var title = Path.GetFileNameWithoutExtension(path);

        // Reading other pages happens off the UI thread; the tree (titles, locations) is only read here.
        var (outgoing, backlinks, mentions) = await Task.Run(() =>
        {
            var outgoingLinks = PageLinks.Outgoing(text, path, allPages, root);
            var backlinkLines = new List<(string Path, LinkLine Line)>();
            var mentionLines = new List<(string Path, LinkLine Line)>();
            foreach (var candidate in Candidates(indexes, title, path))
            {
                string? other = buffers.GetValueOrDefault(candidate);
                try
                {
                    other ??= TextDocumentFile.Load(candidate).Text;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var linking = PageLinks.LinksTo(other, candidate, path, allPages, roots.GetValueOrDefault(candidate));
                backlinkLines.AddRange(linking.Select(l => (candidate, l)));
                var linkedLines = linking.Select(l => l.Line).ToHashSet();
                mentionLines.AddRange(PageLinks.Mentions(other, title).Where(m => !linkedLines.Contains(m.Line)).Select(m => (candidate, m)));
            }

            return (outgoingLinks, backlinkLines, mentionLines);
        });

        if (version != _linksVersion)
        {
            return; // another page became active meanwhile
        }

        Replace(OutgoingLinks, outgoing
            .Select(l => new LinkEntry(
                l.ResolvedPath is { } resolved ? Path.GetFileNameWithoutExtension(resolved) : l.Target,
                l.State switch
                {
                    LinkState.Broken => "missing",
                    LinkState.External => "web",
                    _ => LocationOf(l.ResolvedPath!),
                },
                l.ResolvedPath ?? "",
                l.Line,
                l.Target,
                l.State,
                l.IsWiki ? WikiLinkParser.Scheme + l.Target : l.Target))
            .DistinctBy(l => (l.Path, l.Target)));
        Replace(Backlinks, backlinks.Select(b => new LinkEntry(Path.GetFileNameWithoutExtension(b.Path), LocationOf(b.Path), b.Path, b.Line.Line, b.Line.Text)));
        Replace(UnlinkedMentions, mentions.Select(m => new LinkEntry(Path.GetFileNameWithoutExtension(m.Path), LocationOf(m.Path), m.Path, m.Line.Line, m.Line.Text)));
        LinksMessage = "";
    }

    /// <summary>Pages that may link to or mention the title: index hits for its words (a superset), up to 300.</summary>
    private static IEnumerable<string> Candidates(IReadOnlyList<NotebookIndex> indexes, string title, string self)
    {
        var query = SearchQuery.Parse(string.Join(' ', title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('"', '-', '#'))));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { self };
        foreach (var index in indexes)
        {
            IReadOnlyList<string> found;
            try
            {
                found = index.Candidates(query, null, 300, includeAttachments: false);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
            {
                continue;
            }

            foreach (var page in found.Where(seen.Add))
            {
                yield return page;
            }
        }
    }

    private static void Replace(ObservableCollection<LinkEntry> target, IEnumerable<LinkEntry> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }

    /// <summary>Opens the page a Links pane row refers to, at its line (or follows the active page's link).</summary>
    public void OpenLinkEntry(LinkEntry entry)
    {
        if (entry.Target is { } target && ActiveTab is { } tab && OutgoingLinks.Contains(entry))
        {
            OpenLink(tab, target);
        }
        else
        {
            OpenDocument(entry.Path, preview: false, line: entry.Line);
        }
    }

    /// <summary>Turns an unlinked mention into a wiki link to the active page (in the open tab, or in the file).</summary>
    public void LinkMention(LinkEntry mention)
    {
        if (ActiveTab?.Path is not { } path)
        {
            return;
        }

        var title = Path.GetFileNameWithoutExtension(path);
        if (OpenTabs.FirstOrDefault(t => t.IsAt(mention.Path)) is { } tab)
        {
            var updated = PageLinks.LinkMention(tab.Document.Text, mention.Line, title);
            if (updated != tab.Document.Text)
            {
                var line = tab.Document.GetLineByNumber(mention.Line);
                var newLine = updated.Split('\n')[mention.Line - 1].TrimEnd('\r');
                tab.Document.Replace(line.Offset, line.Length, newLine);
                if (AutosaveEligible(tab))
                {
                    SaveTab(tab, interactive: false);
                }
            }
        }
        else
        {
            try
            {
                var loaded = TextDocumentFile.Load(mention.Path);
                var updated = PageLinks.LinkMention(loaded.Text, mention.Line, title);
                if (updated != loaded.Text)
                {
                    History?.Record(mention.Path, loaded.Text, Core.History.VersionReason.BeforeEditing);
                    TextDocumentFile.Save(mention.Path, updated, loaded.Format);
                    _search?.Refresh(mention.Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError($"Couldn't add the link:\n{ex.Message}");
                return;
            }
        }

        UnlinkedMentions.Remove(mention);
        StatusMessage = $"Linked a mention in {mention.Title}";
        _linksTimer?.Stop();
        _linksTimer?.Start();
    }
}
