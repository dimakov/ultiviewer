using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ultiviewer.Core.Markdown;
using Ultiviewer.Core.Text;

namespace Ultiviewer.App.Editor.Live;

/// <summary>Images and tables drawn in place of their Markdown in the Live view.</summary>
internal static partial class LiveWidgets
{
    private static readonly Dictionary<string, (DateTime Stamp, BitmapSource Image)> LocalImages = [];
    private static readonly Dictionary<string, BitmapImage> RemoteImages = [];

    /// <summary>
    /// The picture for <c>![alt](url)</c>, at most <paramref name="maxWidth"/> wide. <c>![alt|300](url)</c> sets the
    /// width, as in Obsidian. <paramref name="loaded"/> runs when a remote image finishes downloading.
    /// </summary>
    public static FrameworkElement Image(LiveSpan span, string? baseDirectory, LiveStyles styles, double maxWidth, Action loaded, Action reveal)
    {
        var alt = span.Detail ?? "";
        double? width = null;
        if (SizeSuffix().Match(alt) is { Success: true } size)
        {
            width = double.Parse(size.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture);
            alt = alt[..size.Index];
        }

        var source = Load(span.Text ?? "", baseDirectory, loaded);
        FrameworkElement content;
        if (source is null)
        {
            content = new TextBlock
            {
                Text = $"🖼 {(alt.Length > 0 ? alt : span.Text)} (not found)",
                Foreground = styles.Faint,
                FontStyle = FontStyles.Italic,
            };
        }
        else
        {
            var natural = source is BitmapImage { IsDownloading: true } ? maxWidth : source.Width;
            content = new Image
            {
                FlowDirection = FlowDirection.LeftToRight, // pictures are never mirrored in right-to-left notes
                Source = source,
                Stretch = Stretch.Uniform,
                Width = Math.Max(16, Math.Min(width ?? natural, maxWidth)),
                ToolTip = alt.Length > 0 ? alt : null,
            };
            RenderOptions.SetBitmapScalingMode(content, BitmapScalingMode.HighQuality);
        }

        var host = new Border { Child = content, Margin = new Thickness(0, 4, 0, 4), Cursor = Cursors.Arrow, Background = Brushes.Transparent };
        host.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            reveal();
        };
        return host;
    }

    private static BitmapSource? Load(string url, string? baseDirectory, Action loaded)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            {
                if (!RemoteImages.TryGetValue(url, out var remote))
                {
                    remote = new BitmapImage();
                    remote.BeginInit();
                    remote.UriSource = absolute;
                    remote.CacheOption = BitmapCacheOption.OnLoad;
                    remote.EndInit();
                    RemoteImages[url] = remote;
                }

                if (remote.IsDownloading)
                {
                    remote.DownloadCompleted += (_, _) => loaded();
                }

                return remote;
            }

            var path = Uri.UnescapeDataString(url.Split('?', '#')[0]).Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(path))
            {
                if (baseDirectory is null)
                {
                    return null;
                }

                path = Path.GetFullPath(Path.Combine(baseDirectory, path));
            }

            if (!File.Exists(path))
            {
                return null;
            }

            var stamp = File.GetLastWriteTimeUtc(path);
            if (LocalImages.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            {
                return cached.Image;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
            LocalImages[path] = (stamp, image);
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or UriFormatException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>A rendered table; clicking a cell puts the caret at the end of that cell's text.</summary>
    public static FrameworkElement Table(MarkdownTable table, LiveStyles styles, double fontSize, double maxWidth, Action<int> placeCaret)
    {
        var grid = new Grid();
        for (var c = 0; c < table.ColumnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var rows = new List<IReadOnlyList<MarkdownTableCell>> { table.Header };
        rows.AddRange(table.Rows);
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < table.ColumnCount; c++)
            {
                var cell = c < rows[r].Count ? rows[r][c] : null;
                var text = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = Math.Max(120, maxWidth / Math.Max(1, table.ColumnCount) * 1.6),
                    FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    TextAlignment = table.Alignments[c] switch
                    {
                        TableAlignment.Center => TextAlignment.Center,
                        TableAlignment.Right => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    },
                };
                text.Inlines.AddRange(Inlines(cell?.Text ?? "", styles));
                var border = new Border
                {
                    Child = text,
                    Padding = new Thickness(10, 5, 10, 5),
                    BorderBrush = styles.Border,
                    BorderThickness = new Thickness(0, 0, c < table.ColumnCount - 1 ? 1 : 0, r < rows.Count - 1 ? 1 : 0),
                    Background = r == 0 ? styles.HeaderBackground : Brushes.Transparent,
                    Cursor = Cursors.IBeam,
                };
                var caret = cell is null ? (int?)null : cell.Start + cell.Length;
                border.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    if (caret is { } offset)
                    {
                        placeCaret(offset);
                    }
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        // A table reads in its own direction: Hebrew tables put the first column on the right.
        var cellText = string.Join("\n", rows.Select(row => string.Join(" ", row.Select(cell => cell.Text))));
        return new Border
        {
            Child = grid,
            FlowDirection = BidiText.Detect(cellText) == true ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            BorderBrush = styles.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = maxWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
        }.WithFont(styles.TextFont, fontSize);
    }

    private static Border WithFont(this Border border, FontFamily family, double size)
    {
        TextElement.SetFontFamily(border, family);
        TextElement.SetFontSize(border, size);
        return border;
    }

    /// <summary>One line of Markdown as WPF inlines, styled the way the Live view styles it.</summary>
    public static IEnumerable<Inline> Inlines(string markdown, LiveStyles styles)
    {
        var markup = LiveMarkup.Analyze(markdown);
        var spans = markup.SpansOnLine(1);
        var hidden = new bool[markdown.Length];
        foreach (var span in spans.Where(s => s.IsReplacement))
        {
            for (var i = span.Start; i < span.End && i < hidden.Length; i++)
            {
                hidden[i] = span.Style == LiveStyle.Marker;
            }
        }

        var position = 0;
        while (position < markdown.Length)
        {
            if (hidden[position])
            {
                position++;
                continue;
            }

            var active = spans.Where(s => !s.IsReplacement && s.Start <= position && position < s.End).ToList();
            var end = position + 1;
            while (end < markdown.Length && !hidden[end] && spans.All(s => s.IsReplacement || (s.Start <= end && end < s.End) == (s.Start <= position && position < s.End)))
            {
                end++;
            }

            var run = new Run(markdown[position..end]);
            foreach (var span in active)
            {
                Style(run, span, styles);
            }

            yield return run;
            position = end;
        }
    }

    private static void Style(Run run, LiveSpan span, LiveStyles styles)
    {
        switch (span.Style)
        {
            case LiveStyle.Strong:
                run.FontWeight = FontWeights.Bold;
                break;
            case LiveStyle.Emphasis:
                run.FontStyle = FontStyles.Italic;
                break;
            case LiveStyle.Strikethrough:
                run.TextDecorations = TextDecorations.Strikethrough;
                break;
            case LiveStyle.Underline:
                run.TextDecorations = TextDecorations.Underline;
                break;
            case LiveStyle.Highlight:
                run.Background = styles.Highlight;
                break;
            case LiveStyle.Code:
                run.FontFamily = styles.CodeFont;
                run.Background = styles.CodeBackground;
                break;
            case LiveStyle.Link:
                run.Foreground = styles.Link;
                break;
            case LiveStyle.Tag:
                run.Foreground = styles.TagForeground;
                break;
            case LiveStyle.Color when styles.ColorBrush(span.Text) is { } brush:
                run.Foreground = brush;
                break;
            case LiveStyle.Subscript:
                run.BaselineAlignment = BaselineAlignment.Subscript;
                break;
            case LiveStyle.Superscript:
                run.BaselineAlignment = BaselineAlignment.Superscript;
                break;
        }
    }

    [GeneratedRegex(@"\|(?<w>\d{1,4})(?:x\d{1,4})?$")]
    private static partial Regex SizeSuffix();
}
