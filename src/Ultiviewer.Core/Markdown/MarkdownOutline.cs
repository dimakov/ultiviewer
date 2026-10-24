using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Markdown;

/// <summary>A heading and its section: lines are 0-based; <see cref="EndLine"/> is the last line before the next heading of the same or a higher level.</summary>
public readonly record struct OutlineHeading(int Level, string Text, int Line, int EndLine);

public enum FoldKind
{
    Section,
    Code,
    List,
    FrontMatter,
}

/// <summary>A foldable range of 0-based lines; the start line stays visible, the rest folds.</summary>
public readonly record struct FoldRange(int StartLine, int EndLine, FoldKind Kind);

/// <summary>
/// The structure of a Markdown page, from a quick line scan (front matter, fenced code, ATX and setext headings,
/// list items): the outline, fold ranges, and structural edits that move or indent whole sections and list
/// subtrees (in the spirit of Obsidian's Outliner).
/// </summary>
public static partial class MarkdownOutline
{
    public static IReadOnlyList<string> Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    public static IReadOnlyList<OutlineHeading> Headings(string text) => Headings(Lines(text));

    public static IReadOnlyList<OutlineHeading> Headings(IReadOnlyList<string> lines)
    {
        var kinds = Classify(lines);
        var found = new List<(int Level, string Text, int Line)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (kinds[i] is { } level)
            {
                var text = level.Setext ? lines[i].Trim() : AtxHeading().Match(lines[i]).Groups["text"].Value.Trim();
                found.Add((level.Level, text, i));
            }
        }

        var result = new List<OutlineHeading>(found.Count);
        for (var h = 0; h < found.Count; h++)
        {
            var end = lines.Count - 1;
            for (var next = h + 1; next < found.Count; next++)
            {
                if (found[next].Level <= found[h].Level)
                {
                    end = found[next].Line - 1;
                    break;
                }
            }

            result.Add(new OutlineHeading(found[h].Level, found[h].Text, found[h].Line, end));
        }

        return result;
    }

    /// <summary>Heading sections, fenced code blocks, front matter and list items with children.</summary>
    public static IReadOnlyList<FoldRange> Folds(string text)
    {
        var lines = Lines(text);
        var folds = new List<FoldRange>();
        foreach (var heading in Headings(lines))
        {
            var end = TrimBlank(lines, heading.Line, heading.EndLine);
            if (end > heading.Line)
            {
                folds.Add(new FoldRange(heading.Line, end, FoldKind.Section));
            }
        }

        var state = Scan(lines);
        foreach (var (start, end, kind) in state.Blocks)
        {
            folds.Add(new FoldRange(start, end, kind));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (!state.InBlock[i] && ListItem().IsMatch(lines[i]) && ListSubtree(lines, i) is { } subtree && subtree.Last > i)
            {
                folds.Add(new FoldRange(i, subtree.Last, FoldKind.List));
            }
        }

        return [.. folds.OrderBy(f => f.StartLine).ThenByDescending(f => f.EndLine)];
    }

    /// <summary>A list item and the more-indented lines under it (blank lines inside are kept, trailing ones not).</summary>
    public static (int First, int Last)? ListSubtree(IReadOnlyList<string> lines, int index)
    {
        if (index < 0 || index >= lines.Count || !ListItem().IsMatch(lines[index]))
        {
            return null;
        }

        var indent = Indent(lines[index]);
        var last = index;
        for (var j = index + 1; j < lines.Count; j++)
        {
            if (lines[j].Trim().Length == 0)
            {
                continue;
            }

            if (Indent(lines[j]) <= indent)
            {
                break;
            }

            last = j;
        }

        return (index, last);
    }

    /// <summary>
    /// Moves the heading section or list item (with its children) at the caret above the previous / below the
    /// next sibling, keeping blank-line spacing in place. Null when the caret line is neither or there is no
    /// sibling to swap with (plain lines move one line at a time elsewhere).
    /// </summary>
    public static TextEdit? MoveBlock(string text, int caret, bool up, string newLine)
    {
        var lines = Lines(text);
        var starts = LineStarts(text);
        var line = LineOf(starts, caret);
        if (BlockAt(lines, line) is not { } block)
        {
            return null;
        }

        (int First, int Last)? other = up ? PreviousSibling(lines, block) : NextSibling(lines, block);
        if (other is not { } sibling)
        {
            return null;
        }

        var (a, b) = up ? (sibling, block.Range) : (block.Range, sibling);
        var aCore = TrimBlank(lines, a.First, a.Last);
        var bCore = TrimBlank(lines, b.First, b.Last);
        IEnumerable<string> Take(int first, int last) => Enumerable.Range(first, Math.Max(0, last - first + 1)).Select(i => lines[i]);

        // New order: b's lines, the gap that followed a, a's lines, the gap that followed b.
        var moved = Take(b.First, bCore).Concat(Take(aCore + 1, a.Last)).Concat(Take(a.First, aCore)).Concat(Take(bCore + 1, b.Last)).ToList();
        var replaceStart = starts[a.First];
        var replaceEnd = b.Last + 1 < starts.Count ? starts[b.Last + 1] - LineBreakLength(text, starts[b.Last + 1]) : text.Length;
        var replacement = string.Join(newLine, moved);

        // The caret keeps its place inside the moved block.
        var blockFirstNew = up ? 0 : (bCore - b.First + 1) + (a.Last - aCore);
        var offsetInBlock = caret - starts[block.Range.First];
        var newBlockStart = replaceStart + moved.Take(blockFirstNew).Sum(l => l.Length + newLine.Length);
        return new TextEdit(replaceStart, replaceEnd - replaceStart, replacement, newBlockStart + offsetInBlock, 0);
    }

    /// <summary>Indents (or outdents) the list item at the caret together with its children. Null when not on a list item.</summary>
    public static TextEdit? IndentListItem(string text, int caret, bool outdent, string indentUnit, int tabSize = 4)
    {
        var lines = Lines(text);
        var starts = LineStarts(text);
        var line = LineOf(starts, caret);
        if (ListSubtree(lines, line) is not { } subtree)
        {
            return null;
        }

        var changed = new List<string>();
        var caretShift = 0;
        for (var i = subtree.First; i <= subtree.Last; i++)
        {
            var current = lines[i];
            string updated;
            if (!outdent)
            {
                updated = current.Length == 0 ? current : indentUnit + current;
            }
            else
            {
                var remove = current.StartsWith('\t') ? 1 : current.TakeWhile(c => c == ' ').Take(tabSize).Count();
                updated = current[remove..];
            }

            if (i == line)
            {
                caretShift = updated.Length - current.Length;
            }

            changed.Add(updated);
        }

        if (changed.SequenceEqual(Enumerable.Range(subtree.First, subtree.Last - subtree.First + 1).Select(i => lines[i])))
        {
            return null;
        }

        var start = starts[subtree.First];
        var end = subtree.Last + 1 < starts.Count ? starts[subtree.Last + 1] - LineBreakLength(text, starts[subtree.Last + 1]) : text.Length;
        var lineBreak = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // The caret line is the subtree's first line, so only its own indent change moves the caret.
        return new TextEdit(start, end - start, string.Join(lineBreak, changed), Math.Max(starts[line], caret + caretShift), 0);
    }

    // ---- scanning ----

    private sealed record ScanState(bool[] InBlock, List<(int Start, int End, FoldKind Kind)> Blocks);

    /// <summary>Marks lines inside front matter and fenced code, and lists those blocks.</summary>
    private static ScanState Scan(IReadOnlyList<string> lines)
    {
        var inBlock = new bool[lines.Count];
        var blocks = new List<(int, int, FoldKind)>();
        var i = 0;
        if (lines.Count > 1 && lines[0].TrimEnd() == "---")
        {
            var close = Enumerable.Range(1, lines.Count - 1).FirstOrDefault(j => lines[j].TrimEnd() is "---" or "...", -1);
            if (close > 0)
            {
                for (var j = 0; j <= close; j++)
                {
                    inBlock[j] = true;
                }

                blocks.Add((0, close, FoldKind.FrontMatter));
                i = close + 1;
            }
        }

        for (; i < lines.Count; i++)
        {
            var open = Fence().Match(lines[i]);
            if (!open.Success)
            {
                continue;
            }

            var fence = open.Groups["fence"].Value;
            var end = lines.Count - 1;
            for (var j = i + 1; j < lines.Count; j++)
            {
                var close = Fence().Match(lines[j]);
                if (close.Success && close.Groups["fence"].Value[0] == fence[0] && close.Groups["fence"].Length >= fence.Length && close.Groups["rest"].Value.Trim().Length == 0)
                {
                    end = j;
                    break;
                }
            }

            for (var j = i; j <= end; j++)
            {
                inBlock[j] = true;
            }

            if (end > i)
            {
                blocks.Add((i, end, FoldKind.Code));
            }

            i = end;
        }

        return new ScanState(inBlock, blocks);
    }

    private static (int Level, bool Setext)?[] Classify(IReadOnlyList<string> lines)
    {
        var inBlock = Scan(lines).InBlock;
        var result = new (int, bool)?[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (inBlock[i])
            {
                continue;
            }

            if (AtxHeading().Match(lines[i]) is { Success: true } atx)
            {
                result[i] = (atx.Groups["hashes"].Length, false);
            }
            else if (i + 1 < lines.Count && !inBlock[i + 1] && SetextUnderline().Match(lines[i + 1]) is { Success: true } underline
                && IsPlainText(lines[i]) && (underline.Groups["bar"].Value[0] == '=' || (i == 0 || lines[i - 1].Trim().Length == 0)))
            {
                result[i] = (underline.Groups["bar"].Value[0] == '=' ? 1 : 2, true);
            }
        }

        return result;
    }

    private static bool IsPlainText(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0 && !ListItem().IsMatch(line) && trimmed[0] is not ('>' or '|' or '#') && line.Length - trimmed.Length < 4;
    }

    private sealed record Block((int First, int Last) Range, int? HeadingLevel, int? ListIndent);

    private static Block? BlockAt(IReadOnlyList<string> lines, int line)
    {
        if (Headings(lines).FirstOrDefault(h => h.Line == line) is { Text: not null } heading)
        {
            return new Block((heading.Line, heading.EndLine), heading.Level, null);
        }

        if (!Scan(lines).InBlock[line] && ListSubtree(lines, line) is { } subtree)
        {
            return new Block(ExtendWithBlanks(lines, subtree), null, Indent(lines[line]));
        }

        return null;
    }

    private static (int First, int Last)? PreviousSibling(IReadOnlyList<string> lines, Block block)
    {
        var first = block.Range.First;
        if (block.HeadingLevel is { } level)
        {
            var previous = Headings(lines).LastOrDefault(h => h.Line < first && h.Level <= level);
            return previous.Text is not null && previous.Level == level ? (previous.Line, first - 1) : null;
        }

        var indent = block.ListIndent!.Value;
        for (var k = first - 1; k >= 0; k--)
        {
            if (lines[k].Trim().Length == 0 || Indent(lines[k]) > indent)
            {
                continue;
            }

            return Indent(lines[k]) == indent && ListItem().IsMatch(lines[k]) ? (k, first - 1) : null;
        }

        return null;
    }

    private static (int First, int Last)? NextSibling(IReadOnlyList<string> lines, Block block)
    {
        var next = block.Range.Last + 1;
        if (next >= lines.Count)
        {
            return null;
        }

        if (block.HeadingLevel is { } level)
        {
            var heading = Headings(lines).FirstOrDefault(h => h.Line == next);
            return heading.Text is not null && heading.Level == level ? (heading.Line, heading.EndLine) : null;
        }

        return ListItem().IsMatch(lines[next]) && Indent(lines[next]) == block.ListIndent && ListSubtree(lines, next) is { } subtree
            ? ExtendWithBlanks(lines, subtree)
            : null;
    }

    /// <summary>A list subtree plus the blank lines after it, when another item at the same level follows them.</summary>
    private static (int First, int Last) ExtendWithBlanks(IReadOnlyList<string> lines, (int First, int Last) subtree)
    {
        var last = subtree.Last;
        while (last + 1 < lines.Count && lines[last + 1].Trim().Length == 0)
        {
            last++;
        }

        return last + 1 < lines.Count && ListItem().IsMatch(lines[last + 1]) && Indent(lines[last + 1]) == Indent(lines[subtree.First]) ? (subtree.First, last) : subtree;
    }

    private static int TrimBlank(IReadOnlyList<string> lines, int first, int last)
    {
        while (last > first && lines[last].Trim().Length == 0)
        {
            last--;
        }

        return last;
    }

    private static int Indent(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                width++;
            }
            else if (c == '\t')
            {
                width += 4 - width % 4;
            }
            else
            {
                break;
            }
        }

        return width;
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int LineOf(List<int> starts, int offset)
    {
        var index = starts.BinarySearch(offset);
        return index >= 0 ? index : ~index - 1;
    }

    private static int LineBreakLength(string text, int nextLineStart) =>
        nextLineStart >= 2 && text[nextLineStart - 2] == '\r' && text[nextLineStart - 1] == '\n' ? 2 : 1;

    [GeneratedRegex(@"^ {0,3}(?<hashes>#{1,6})(?:[ \t]+(?<text>.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"^ {0,3}(?<bar>=+|-+)[ \t]*$")]
    private static partial Regex SetextUnderline();

    [GeneratedRegex(@"^[ \t]*(?:[-*+]|\d{1,9}[.)])(?:[ \t]|$)")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^[ \t]*(?<fence>`{3,}|~{3,})(?<rest>.*)$")]
    private static partial Regex Fence();
}
