using System.Text.RegularExpressions;

namespace Ultiviewer.Core.Markdown;

/// <summary>Edits Markdown task list items (<c>- [ ] text</c> / <c>- [x] text</c>) on a single line.</summary>
public static partial class TaskMarkers
{
    /// <summary>Flips <c>[ ]</c> ↔ <c>[x]</c>. Returns the line unchanged if it isn't a task.</summary>
    public static string Toggle(string line)
    {
        var match = Task().Match(line);
        if (!match.Success)
        {
            return line;
        }

        var box = match.Groups["box"];
        var replacement = box.Value == " " ? "x" : " ";
        return line[..box.Index] + replacement + line[(box.Index + box.Length)..];
    }

    /// <summary>
    /// OneNote's Ctrl+1: plain line → <c>- [ ] line</c> (a bullet keeps its marker), open task → done task,
    /// done task → plain list item.
    /// </summary>
    public static string Cycle(string line)
    {
        var task = Task().Match(line);
        if (task.Success)
        {
            if (task.Groups["box"].Value == " ")
            {
                return Toggle(line);
            }

            // Done → remove the checkbox, keep the bullet.
            var end = task.Groups["box"].Index + 2; // past "x]"
            var rest = line[end..].TrimStart(' ');
            return task.Groups["prefix"].Value + rest;
        }

        var bullet = Bullet().Match(line);
        if (bullet.Success)
        {
            return bullet.Groups["prefix"].Value + "[ ] " + line[bullet.Length..];
        }

        var indent = line.Length - line.TrimStart(' ', '\t').Length;
        return line[..indent] + "- [ ] " + line[indent..];
    }

    public static bool IsTask(string line, out bool done)
    {
        var match = Task().Match(line);
        done = match.Success && match.Groups["box"].Value != " ";
        return match.Success;
    }

    [GeneratedRegex(@"^(?<prefix>[ \t]*(?:[-*+]|\d+[.)])[ \t]+)\[(?<box>[ xX])\]")]
    private static partial Regex Task();

    [GeneratedRegex(@"^(?<prefix>[ \t]*(?:[-*+]|\d+[.)])[ \t]+)")]
    private static partial Regex Bullet();
}
