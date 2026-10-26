using System.Text;
using Ultiviewer.Core.OneNote.Store;

namespace Ultiviewer.Core.OneNote;

/// <summary>
/// Turns OneNote files into <see cref="OneNoteNotebook"/> / <see cref="OneNoteSection"/> models, following the object
/// graph of [MS-ONE]: a section's root names its page series, each page is an object space whose page node lists the
/// title and the content (outlines, pictures, files), outlines hold paragraphs (rich text, pictures, tables) with
/// their lists, note tags and indented children.
/// </summary>
public static class OneNoteReader
{
    // Object types (JCIDs, [MS-ONE] 2.1.13).
    private const uint SectionNode = 0x00060007, PageSeriesNode = 0x00060008, PageNode = 0x0006000B, OutlineNode = 0x0006000C,
        OutlineElementNode = 0x0006000D, RichTextNode = 0x0006000E, ImageNode = 0x00060011, NumberListNode = 0x00060012,
        InkContainer = 0x00060014, OutlineGroup = 0x00060019, TableNode = 0x00060022, TableRowNode = 0x00060023,
        TableCellNode = 0x00060024, TitleNode = 0x0006002C, EmbeddedFileNode = 0x00060035, PageManifestNode = 0x00060037,
        IFrameNode = 0x00060058, NoteTagDefinition = 0x00120043, TocContainer = 0x00020001;

    // Property ids ([MS-ONE] 2.1.12); only the low 26 bits are compared.
    private const uint ElementChildNodes = 0x24001C20, ContentChildNodes = 0x24001C1F, StructureElementChildNodes = 0x24001D5F,
        ChildGraphSpaceElementNodes = 0x2C001D63, ListNodes = 0x24001C26, RichEditTextUnicode = 0x1C001C22,
        TextExtendedAscii = 0x1C003498, TextRunIndex = 0x1C001E12, TextRunFormatting = 0x24001E13, ParagraphStyle = 0x2000342C,
        ParagraphStyleId = 0x1C00345A, Bold = 0x08001C04, Italic = 0x08001C05, Underline = 0x08001C06, Strikethrough = 0x08001C07,
        Superscript = 0x08001C08, Subscript = 0x08001C09, Font = 0x1C001C0A, FontColor = 0x14001C0C, Highlight = 0x14001C0D,
        Hyperlink = 0x08001E14, Hidden = 0x08001E16, MathFormatting = 0x08003401, NumberListFormat = 0x1C001C1A,
        ListRestart = 0x14001CB7, CachedTitleString = 0x1C001CF3, PageLevel = 0x14001DFF, TopologyCreationTimeStamp = 0x18001C65,
        LastModifiedTime = 0x14001D7A, OffsetFromParentHoriz = 0x14001C14, OffsetFromParentVert = 0x14001C15,
        PictureContainer = 0x20001C3F, ImageAltText = 0x1C001E58, ImageFilename = 0x1C001DD7, WzHyperlinkUrl = 0x1C001E20,
        EmbeddedFileContainer = 0x20001D9B, EmbeddedFileName = 0x1C001D9C, SourceFilepath = 0x1C001D9D, NoteTags = 0x40003489,
        NoteTagDefinitionOid = 0x20003488, NoteTagLabel = 0x1C003468, NoteTagShape = 0x10003464, ActionItemStatus = 0x10003470,
        SectionDisplayName = 0x1C00349B, TocChildren = 0x24001CF6, FolderChildFilename = 0x1C001D6B, NotebookElementOrderingId = 0x14001CB9,
        IsTitleText = 0x08001CB4;

    private const int MaxDepth = 64;

    /// <summary>Note tag shapes drawn as check boxes ([MS-ONE] 2.3.x NoteTagShape): these tags are to-dos.</summary>
    private static readonly HashSet<int> CheckboxShapes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 28, 30, 32, 48, 50, 52, 69, 71, 73, 94, 95, 96, 97, 98, 99];

    /// <summary>Reads a .one section file. Problems that affect the whole section become <see cref="OneNoteSection.Problem"/>.</summary>
    public static OneNoteSection ReadSection(string path)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(path);
        try
        {
            var file = OneStoreLoader.Load(File.ReadAllBytes(path), Path.GetDirectoryName(path));
            return ReadSection(file, fallbackName);
        }
        catch (OneNoteFormatException ex)
        {
            return new OneNoteSection(fallbackName, [], ex.Message);
        }
    }

    public static OneNoteSection ReadSection(OneStoreFile file, string fallbackName)
    {
        var root = file.Root;
        var name = root.Get(root.MetadataRoot)?.Properties.String(SectionDisplayName) is { Length: > 0 } displayName ? displayName : fallbackName;
        if (name.Length > 4 && name.EndsWith(".one", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4]; // OneNote for the web stores the file name
        }

        var section = root.Get(root.ContentRoot);
        if (section?.Jcid != SectionNode)
        {
            return new OneNoteSection(name, [], "The file doesn't contain a OneNote section.");
        }

        var pages = new List<OneNotePage>();
        foreach (var seriesId in section.Properties.References(ElementChildNodes))
        {
            if (root.Get(seriesId) is not { Jcid: PageSeriesNode } series)
            {
                continue;
            }

            foreach (var spaceId in series.Properties.References(ChildGraphSpaceElementNodes))
            {
                if (file.Space(spaceId) is { } space && ReadPage(space) is { } page)
                {
                    pages.Add(page);
                }
            }
        }

        return new OneNoteSection(name, pages);
    }

    /// <summary>The section files and section groups in a .onetoc2, in OneNote's order (file and folder names).</summary>
    public static IReadOnlyList<string> ReadTableOfContents(string path)
    {
        try
        {
            var file = OneStoreLoader.Load(File.ReadAllBytes(path), Path.GetDirectoryName(path));
            var entries = new List<(uint Order, int Index, string Name)>();
            CollectToc(file.Root, file.Root.ContentRoot, entries, 0);

            // A name can appear more than once; the last occurrence wins, then entries sort by their ordering id.
            var latest = new Dictionary<string, (uint Order, int Index)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (order, index, entryName) in entries)
            {
                latest[entryName] = (order, index);
            }

            return [.. latest.OrderBy(e => e.Value.Order).ThenBy(e => e.Value.Index).Select(e => e.Key)];
        }
        catch (Exception ex) when (ex is OneNoteFormatException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void CollectToc(ObjectSpace space, ExGuid? id, List<(uint, int, string)> entries, int depth)
    {
        if (depth > MaxDepth || space.Get(id) is not { Jcid: TocContainer } toc)
        {
            return;
        }

        if (toc.Properties.String(FolderChildFilename) is { Length: > 0 } fileName)
        {
            var order = (uint)(toc.Properties.Number(NotebookElementOrderingId) ?? uint.MaxValue);
            entries.Add((order, entries.Count, fileName.Replace("^M", "+").Replace("^J", ",")));
        }

        foreach (var child in toc.Properties.References(TocChildren))
        {
            CollectToc(space, child, entries, depth + 1);
        }
    }

    // ---- Pages ----

    private static OneNotePage? ReadPage(ObjectSpace space)
    {
        var metadata = space.Get(space.MetadataRoot)?.Properties ?? PropertySet.Empty;
        var manifest = space.Get(space.ContentRoot);
        var page = manifest?.Jcid == PageManifestNode
            ? manifest.Properties.References(ContentChildNodes).Select(id => space.Get(id)).FirstOrDefault(o => o?.Jcid == PageNode)
            : manifest?.Jcid == PageNode ? manifest : null;
        if (page is null)
        {
            return null;
        }

        var title = metadata.String(CachedTitleString);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = page.Properties.References(StructureElementChildNodes).Select(id => space.Get(id))
                .Where(o => o?.Jcid == TitleNode)
                .Select(o => TitleText(space, o!))
                .FirstOrDefault(t => t.Length > 0);
        }

        var created = metadata.Number(TopologyCreationTimeStamp) is { } fileTime and > 0 and < 2650467743999999999 ? DateTime.FromFileTimeUtc((long)fileTime) : (DateTime?)null;
        var modified = Time32(page.Properties.Number(LastModifiedTime));

        var visited = new HashSet<ExGuid>();
        var blocks = page.Properties.References(ElementChildNodes)
            .Select(id => (Object: space.Get(id), Id: id))
            .Where(x => x.Object is not null)
            .Select(x => (Block: ReadBlock(space, x.Object!, x.Id, visited), Y: x.Object!.Properties.Float(OffsetFromParentVert) ?? 0, X: x.Object.Properties.Float(OffsetFromParentHoriz) ?? 0))
            .Where(x => x.Block is not null)
            .OrderBy(x => x.Y)
            .ThenBy(x => x.X)
            .Select(x => x.Block!)
            .ToList();

        var level = (int)Math.Clamp(metadata.Number(PageLevel) ?? 1, 1, 3);
        return new OneNotePage(title?.Trim() ?? "", level, created, modified, blocks);
    }

    private static string TitleText(ObjectSpace space, StoreObject title)
    {
        var visited = new HashSet<ExGuid>();
        var text = new StringBuilder();
        foreach (var outlineId in title.Properties.References(ElementChildNodes))
        {
            if (space.Get(outlineId) is { Jcid: OutlineNode } outline)
            {
                foreach (var element in ReadElements(space, outline.Properties.References(ElementChildNodes), visited, 0))
                {
                    foreach (var content in element.Contents.OfType<OneNoteText>())
                    {
                        text.Append(VisibleText(content)).Append(' ');
                    }
                }
            }

            if (text.Length > 0)
            {
                break; // the first outline is the title; date and time follow in others
            }
        }

        return text.ToString().Trim();
    }

    /// <summary>
    /// The page's name as OneNote lists it: the title, or for an untitled page the start of its first line of
    /// text; empty when the page has neither.
    /// </summary>
    public static string DisplayTitle(OneNotePage page)
    {
        if (page.Title.Length > 0)
        {
            return page.Title;
        }

        static IEnumerable<OneNoteText> Texts(IEnumerable<OneNoteElement> elements) =>
            elements.SelectMany(e => e.Contents.OfType<OneNoteText>().Concat(Texts(e.Children)));

        var first = page.Blocks.OfType<OneNoteOutline>().SelectMany(o => Texts(o.Elements))
            .SelectMany(t => VisibleText(t).Replace("\uFFFC", "").Split('\n', '\u000B'))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? "";
        if (first.Length <= 60)
        {
            return first;
        }

        var cut = first.LastIndexOf(' ', 60);
        return first[..(cut > 20 ? cut : 60)].TrimEnd() + "…";
    }

    /// <summary>The text a reader sees: hidden runs (hyperlink targets) removed.</summary>
    public static string VisibleText(OneNoteText text)
    {
        if (text.Runs.All(r => !r.Style.Hidden))
        {
            return text.Text;
        }

        var builder = new StringBuilder();
        foreach (var run in text.Runs.Where(r => !r.Style.Hidden))
        {
            builder.Append(text.Text, run.Start, run.End - run.Start);
        }

        return builder.ToString();
    }

    private static OneNoteBlock? ReadBlock(ObjectSpace space, StoreObject item, ExGuid id, HashSet<ExGuid> visited) => item.Jcid switch
    {
        OutlineNode => new OneNoteOutline(ReadElements(space, item.Properties.References(ElementChildNodes), visited, 0)),
        _ => ReadContent(space, item, id, visited, 0) is { } content ? new OneNoteFreeContent(content) : null,
    };

    // ---- Outlines ----

    private static List<OneNoteElement> ReadElements(ObjectSpace space, IReadOnlyList<ExGuid> ids, HashSet<ExGuid> visited, int depth)
    {
        var elements = new List<OneNoteElement>();
        if (depth > MaxDepth)
        {
            return elements;
        }

        foreach (var id in ids)
        {
            if (!visited.Add(id) || space.Get(id) is not { } item)
            {
                continue;
            }

            if (item.Jcid == OutlineGroup)
            {
                elements.AddRange(ReadElements(space, item.Properties.References(ElementChildNodes), visited, depth + 1));
                continue;
            }

            if (item.Jcid != OutlineElementNode)
            {
                continue;
            }

            var contents = item.Properties.References(ContentChildNodes)
                .Select(contentId => space.Get(contentId) is { } content ? ReadContent(space, content, contentId, visited, depth + 1) : null)
                .OfType<OneNoteContent>()
                .ToList();
            var children = ReadElements(space, item.Properties.References(ElementChildNodes), visited, depth + 1);
            var list = item.Properties.References(ListNodes).Select(id => space.Get(id)).FirstOrDefault(o => o?.Jcid == NumberListNode) is { } listNode
                ? ReadList(listNode)
                : null;
            elements.Add(new OneNoteElement(contents, children, list));
        }

        return elements;
    }

    /// <summary>A list's format string contains U+FFFD where the number goes; anything else is a bullet character.</summary>
    private static OneNoteList ReadList(StoreObject list)
    {
        var format = list.Properties.String(NumberListFormat) ?? "";
        var numbered = format.Length > 1 && format[1..].Contains('\uFFFD');
        var restart = list.Properties.Number(ListRestart) is { } value and < int.MaxValue ? (int?)value : null;
        return new OneNoteList(numbered, restart);
    }

    private static OneNoteContent? ReadContent(ObjectSpace space, StoreObject item, ExGuid id, HashSet<ExGuid> visited, int depth)
    {
        switch (item.Jcid)
        {
            case RichTextNode:
                return ReadText(space, item);
            case ImageNode:
            {
                var props = item.Properties;
                var container = space.Get(props.Reference(PictureContainer));
                return new OneNoteImage(container?.FileData, container?.FileExtension, props.String(ImageAltText), props.String(ImageFilename),
                    props.String(WzHyperlinkUrl), container is null ? "the picture's data is missing" : container.FileDataProblem);
            }

            case EmbeddedFileNode:
            {
                var props = item.Properties;
                var container = space.Get(props.Reference(EmbeddedFileContainer));
                var name = props.String(EmbeddedFileName) is { Length: > 0 } fileName ? fileName
                    : Path.GetFileName(props.String(SourceFilepath)) is { Length: > 0 } source ? source : "attachment";
                return new OneNoteFile(name, container?.FileData, container is null ? "the file's data is missing" : container.FileDataProblem);
            }

            case TableNode:
            {
                var rows = new List<IReadOnlyList<IReadOnlyList<OneNoteElement>>>();
                foreach (var rowId in item.Properties.References(ElementChildNodes))
                {
                    if (space.Get(rowId) is not { Jcid: TableRowNode } row)
                    {
                        continue;
                    }

                    var cells = new List<IReadOnlyList<OneNoteElement>>();
                    foreach (var cellId in row.Properties.References(ElementChildNodes))
                    {
                        if (space.Get(cellId) is { Jcid: TableCellNode } cell)
                        {
                            cells.Add(ReadElements(space, cell.Properties.References(ElementChildNodes), visited, depth + 1));
                        }
                    }

                    rows.Add(cells);
                }

                return new OneNoteTable(rows);
            }

            case OutlineNode:
                // An outline nested in a paragraph (rare): flatten its paragraphs into a table-less group.
                return new OneNoteTable([[ReadElements(space, item.Properties.References(ElementChildNodes), visited, depth + 1)]]);
            case InkContainer:
                return new OneNoteUnsupported("a handwritten drawing (ink)");
            case IFrameNode:
                return new OneNoteUnsupported("an embedded web content frame");
            default:
                return null;
        }
    }

    // ---- Rich text ----

    private static OneNoteText ReadText(ObjectSpace space, StoreObject item)
    {
        var props = item.Properties;
        var text = props.String(RichEditTextUnicode)
            ?? (props.Bytes(TextExtendedAscii) is { } ascii ? Latin1(ascii) : "");
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var paragraph = space.Get(props.Reference(ParagraphStyle))?.Properties;
        var indexes = props.UInt32Array(TextRunIndex);
        var styles = props.References(TextRunFormatting).Select(id => Style(space.Get(id)?.Properties)).ToList();

        // Run i ends at indexes[i]; the run after the last index ends at the end of the text.
        var runs = new List<OneNoteRun>();
        var start = 0;
        for (var i = 0; i <= indexes.Count; i++)
        {
            var end = i < indexes.Count ? Math.Max((int)Math.Min(indexes[i], (uint)text.Length), start) : text.Length;
            if (end > start)
            {
                runs.Add(new OneNoteRun(start, end, i < styles.Count ? styles[i] : OneNoteStyle.Plain));
            }

            start = end;
        }

        var tags = new List<OneNoteTag>();
        foreach (var tagState in props.Sets(NoteTags))
        {
            if (space.Get(tagState.Reference(NoteTagDefinitionOid)) is not { Jcid: NoteTagDefinition } definition)
            {
                continue;
            }

            var shape = (int)(definition.Properties.Number(NoteTagShape) ?? 0);
            var completed = ((tagState.Number(ActionItemStatus) ?? 0) & 1) != 0;
            tags.Add(new OneNoteTag(definition.Properties.String(NoteTagLabel) ?? "", shape, CheckboxShapes.Contains(shape), completed));
        }

        return new OneNoteText(text, runs, paragraph?.String(ParagraphStyleId), tags);
    }

    private static OneNoteStyle Style(PropertySet? props) => props is null
        ? OneNoteStyle.Plain
        : new OneNoteStyle(
            props.Bool(Bold), props.Bool(Italic), props.Bool(Underline), props.Bool(Strikethrough), props.Bool(Superscript), props.Bool(Subscript),
            props.String(Font), Color(props.Number(FontColor)), Color(props.Number(Highlight)), props.Bool(Hyperlink), props.Bool(Hidden),
            props.Bool(MathFormatting));

    /// <summary>A COLORREF (0x00BBGGRR) as 0xRRGGBB; 0xFFFFFFFF and the "automatic" flag (0xFF000000) mean none.</summary>
    private static uint? Color(ulong? colorRef) => colorRef is { } value && (value >> 24) == 0
        ? (uint)((value & 0xFF) << 16 | (value & 0xFF00) | (value >> 16 & 0xFF))
        : null;

    /// <summary>Time32 ([MS-ONE] 2.3.1): seconds since 1980-01-01 UTC.</summary>
    private static DateTime? Time32(ulong? seconds) => seconds is { } value and > 0
        ? new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(value)
        : null;

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes).TrimEnd('\0');
}
