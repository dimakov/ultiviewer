using System.Text.RegularExpressions;
using Ultiviewer.Core.Links;
using Ultiviewer.Core.Markdown;

namespace Ultiviewer.Core.Search;

public readonly record struct TagOccurrence(string Tag, int Line);

public readonly record struct TaskItem(int Line, bool Done, string Text);

/// <summary>A page property: <paramref name="Key"/> is lower-case; a list property has one entry per value.</summary>
public readonly record struct PageProperty(string Key, string Value, int Line);

/// <summary>
/// Structured facts the index stores besides text: <c>#tags</c> (inline or in front matter <c>tags:</c>), task
/// list items and properties (front matter <c>key: value</c>, and Dataview-style <c>key:: value</c> fields in the
/// text). Code blocks and inline code are ignored. Lines are 1-based.
/// </summary>
public sealed partial class PageFacts
{
    public IReadOnlyList<TagOccurrence> Tags { get; private init; } = [];

    public IReadOnlyList<TaskItem> Tasks { get; private init; } = [];

    public IReadOnlyList<PageProperty> Properties { get; private init; } = [];

    public static PageFacts Extract(string text)
    {
        var tags = new List<TagOccurrence>();
        var tasks = new List<TaskItem>();
        var properties = new List<PageProperty>();
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
                properties.AddRange(FrontMatterProperty(lines, i, frontMatterEnd));
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

                if (line.Contains("::", StringComparison.Ordinal))
                {
                    foreach (Match match in InlineField().Matches(line))
                    {
                        if (!CodeRanges.Contains(code, offset + match.Index) && Unquote(match.Groups["value"].Value) is { Length: > 0 } value)
                        {
                            properties.Add(new PageProperty(match.Groups["key"].Value.ToLowerInvariant(), value, number));
                        }
                    }
                }
            }

            offset += lines[i].Length + 1;
        }

        return new PageFacts { Tags = tags, Tasks = tasks, Properties = properties };
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

    /// <summary>
    /// A top-level front matter line <c>key: value</c>: a scalar, an inline list <c>[a, b]</c>, or (with nothing after
    /// the colon) the <c>- item</c> lines that follow. Nested maps are skipped.
    /// </summary>
    private static IEnumerable<PageProperty> FrontMatterProperty(string[] lines, int index, int end)
    {
        var match = FrontMatterKey().Match(lines[index].TrimEnd('\r'));
        if (!match.Success)
        {
            yield break;
        }

        var key = match.Groups["key"].Value.ToLowerInvariant();
        var value = match.Groups["value"].Value.Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            foreach (var part in value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Unquote(part) is { Length: > 0 } item)
                {
                    yield return new PageProperty(key, item, index + 1);
                }
            }
        }
        else if (value.Length > 0)
        {
            if (Unquote(value) is { Length: > 0 } scalar && scalar is not ("|" or ">" or "{}"))
            {
                yield return new PageProperty(key, scalar, index + 1);
            }
        }
        else
        {
            for (var i = index + 1; i < end - 1; i++)
            {
                var item = FrontMatterListItem().Match(lines[i].TrimEnd('\r'));
                if (!item.Success)
                {
                    break;
                }

                if (Unquote(item.Groups["value"].Value) is { Length: > 0 } listValue)
                {
                    yield return new PageProperty(key, listValue, i + 1);
                }
            }
        }
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'') ? value[1..^1].Trim() : value;
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

    [GeneratedRegex(@"^(?<key>[\p{L}_][\p{L}\p{N}_\-]*)[ \t]*:(?:[ \t]+(?<value>.*)|[ \t]*)$")]
    private static partial Regex FrontMatterKey();

    // Dataview fields: "key:: value" as a whole line (also after a list marker; the space keeps "std::vector"
    // out) or "[key:: value]" / "(key:: value)" inside text.
    [GeneratedRegex(@"^[ \t]*(?:[-*+][ \t]+(?:\[[ xX]\][ \t]+)?)?(?<key>[\p{L}_][\p{L}\p{N}_\-]*)::[ \t]+(?<value>[^\r\n]+?)[ \t]*$|[\[(](?<key>[\p{L}_][\p{L}\p{N}_\-]*)::[ \t]*(?<value>[^\])\r\n]+)[\])]")]
    private static partial Regex InlineField();
}
