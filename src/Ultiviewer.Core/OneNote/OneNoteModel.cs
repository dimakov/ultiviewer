namespace Ultiviewer.Core.OneNote;

/// <summary>A OneNote notebook: its sections and section groups in OneNote's order.</summary>
public sealed record OneNoteNotebook(string Name, IReadOnlyList<OneNoteEntry> Entries);

/// <summary>A section or a section group.</summary>
public abstract record OneNoteEntry(string Name);

public sealed record OneNoteSectionGroup(string Name, IReadOnlyList<OneNoteEntry> Entries) : OneNoteEntry(Name);

/// <summary>A section file of a notebook, read with <see cref="OneNoteReader.ReadSection(string)"/> when needed.</summary>
public sealed record OneNoteSectionFile(string Name, string Path) : OneNoteEntry(Name);

/// <param name="Problem">Why the section couldn't be read (password protection, an unsupported format…); null when it was.</param>
public sealed record OneNoteSection(string Name, IReadOnlyList<OneNotePage> Pages, string? Problem = null);

/// <summary>
/// A page: title (empty when the page has none), subpage level (1 = top level, 2 and 3 = subpages of the page before it), times, and its content
/// in reading order (top to bottom, then left to right).
/// </summary>
public sealed record OneNotePage(string Title, int Level, DateTime? Created, DateTime? Modified, IReadOnlyList<OneNoteBlock> Blocks);

/// <summary>Page content: an outline (a text container) or a free-standing picture, file or drawing.</summary>
public abstract record OneNoteBlock;

public sealed record OneNoteOutline(IReadOnlyList<OneNoteElement> Elements) : OneNoteBlock;

public sealed record OneNoteFreeContent(OneNoteContent Content) : OneNoteBlock;

/// <summary>A paragraph of an outline with its indented children; <paramref name="List"/> makes it a list item.</summary>
public sealed record OneNoteElement(IReadOnlyList<OneNoteContent> Contents, IReadOnlyList<OneNoteElement> Children, OneNoteList? List);

/// <param name="Numbered">A numbered list (the format has a number placeholder) rather than bullets.</param>
public sealed record OneNoteList(bool Numbered, int? Restart);

public abstract record OneNoteContent;

/// <param name="Style">The paragraph style id: "h1"…"h6", "p", "code", "quote", "cite", "PageTitle"…</param>
public sealed record OneNoteText(string Text, IReadOnlyList<OneNoteRun> Runs, string? Style, IReadOnlyList<OneNoteTag> Tags) : OneNoteContent;

/// <summary>A run of text [<paramref name="Start"/>, <paramref name="End"/>) with one formatting.</summary>
public sealed record OneNoteRun(int Start, int End, OneNoteStyle Style);

/// <param name="Color">Font color as 0xRRGGBB; null for automatic.</param>
/// <param name="Highlight">Highlight color as 0xRRGGBB; null for none.</param>
/// <param name="Hidden">Hidden text: OneNote hides a hyperlink's target this way.</param>
public sealed record OneNoteStyle(
    bool Bold = false, bool Italic = false, bool Underline = false, bool Strikethrough = false,
    bool Superscript = false, bool Subscript = false, string? Font = null, uint? Color = null, uint? Highlight = null,
    bool Hyperlink = false, bool Hidden = false, bool Math = false)
{
    public static OneNoteStyle Plain { get; } = new();
}

/// <param name="Checkbox">A to-do style tag (its shape is a check box).</param>
public sealed record OneNoteTag(string Label, int Shape, bool Checkbox, bool Completed);

/// <param name="Problem">Why <paramref name="Data"/> is missing, when it is.</param>
public sealed record OneNoteImage(byte[]? Data, string? Extension, string? AltText, string? FileName, string? Link, string? Problem) : OneNoteContent;

public sealed record OneNoteFile(string Name, byte[]? Data, string? Problem) : OneNoteContent;

/// <summary>A table: rows of cells, each cell a list of paragraphs.</summary>
public sealed record OneNoteTable(IReadOnlyList<IReadOnlyList<IReadOnlyList<OneNoteElement>>> Rows) : OneNoteContent;

/// <summary>Content the import can't convert (ink drawings, …), named for the import report.</summary>
public sealed record OneNoteUnsupported(string What) : OneNoteContent;
