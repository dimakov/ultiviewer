using System.Text.RegularExpressions;
using Ultiviewer.Core.Links;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.Core.Search;

public readonly record struct TagOccurrence(string Tag, int Line);

public readonly record struct TaskItem(int Line, bool Done, string Text);

/// <summary>
/// Structured facts the index stores besides text: <c>#tags</c> (inline or in front matter <c>tags:</c>) and
/// task list items. Code blocks and inline code are ignored. Lines are 1-based.
/// </summary>
public sealed partial class PageFacts
{
    public IReadOnlyList<TagOccurrence> Tags { get; private init; } = [];

    public IReadOnlyList<TaskItem> Tasks { get; private init; } = [];

    public static PageFacts Extract(string text)
    {
        var tags = new List<TagOccurrence>();
        var tasks = new List<TaskItem>();
        var code = CodeRanges.Find(text);
        var lines = text.Split('\n');
        var offset = 0;
        var frontMatterEnd = FrontMatterEnd(lines);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var number = i + 1;
            if (i < frontMatterEnd)
            {
                tags.AddRange(FrontMatterTags(lines, i, frontMatterEnd).Select(t => new TagOccurrence(t, number)));
                offset += lines[i].Length + 1;
                continue;
            }

            if (!CodeRanges.Contains(code, offset))
            {
                foreach (Match match in InlineTag().Matches(line))
                {
                    if (!CodeRanges.Contains(code, offset + match.Index))
                    {
                        tags.Add(new TagOccurrence(match.Groups["tag"].Value.TrimEnd('/', '-').ToLowerInvariant(), number));
                    }
                }

                if (TaskMarkers.IsTask(line, out var done))
                {
                    tasks.Add(new TaskItem(number, done, TaskText().Replace(line, "").Trim()));
                }
            }

            offset += lines[i].Length + 1;
        }

        return new PageFacts { Tags = tags, Tasks = tasks };
    }

    /// <summary>Index of the first line after a <c>---</c> front matter block at the top, or 0.</summary>
    private static int FrontMatterEnd(string[] lines)
    {
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "---")
        {
            return 0;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line is "---" or "...")
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>Handles <c>tags: [a, b]</c>, <c>tags: a, b</c> and a following <c>- a</c> list.</summary>
    private static IEnumerable<string> FrontMatterTags(string[] lines, int index, int end)
    {
        var line = lines[index].TrimEnd('\r');
        var match = FrontMatterTagsKey().Match(line);
        if (!match.Success)
        {
            yield break;
        }

        var value = match.Groups["value"].Value.Trim().Trim('[', ']');
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = part.Trim('"', '\'').TrimStart('#').ToLowerInvariant();
            if (tag.Length > 0)
            {
                yield return tag;
            }
        }

        for (var i = index + 1; i < end - 1; i++)
        {
            var item = FrontMatterListItem().Match(lines[i].TrimEnd('\r'));
            if (!item.Success)
            {
                break;
            }

            yield return item.Groups["value"].Value.Trim('"', '\'').TrimStart('#').ToLowerInvariant();
        }
    }

    // #tag: starts with a letter or underscore (so "#123" issue numbers and "# Heading" are not tags).
    [GeneratedRegex(@"(?<=^|[\s(\[,;])#(?<tag>[\p{L}_][\p{L}\p{N}_\-/]*)")]
    private static partial Regex InlineTag();

    [GeneratedRegex(@"^[ \t]*(?:[-*+]|\d+[.)])[ \t]+\[[ xX]\][ \t]*")]
    private static partial Regex TaskText();

    [GeneratedRegex(@"^tags[ \t]*:(?<value>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FrontMatterTagsKey();

    [GeneratedRegex(@"^[ \t]+-[ \t]+(?<value>\S.*)$")]
    private static partial Regex FrontMatterListItem();
}
