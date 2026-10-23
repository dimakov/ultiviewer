using System.IO;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Ultiviewer.App.Editor;

/// <summary>
/// Syntax highlighting definitions for the editor: AvalonEdit's bundled languages plus our Markdown
/// definition, each available in a light variant (as authored) and a dark variant (dark colors lightened).
/// </summary>
public sealed class HighlightingRegistry
{
    public const string PlainText = "Plain text";

    private const string BuiltInPrefix = "ICSharpCode.AvalonEdit.Highlighting.Resources.";
    private const string MarkdownResource = "Ultiviewer.Markdown.xshd";

    private readonly Dictionary<string, Func<Stream>> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Name, bool Dark), IHighlightingDefinition?> _loaded = [];

    public static HighlightingRegistry Instance { get; } = new();

    private HighlightingRegistry()
    {
        var avalon = typeof(HighlightingManager).Assembly;
        foreach (var resource in avalon.GetManifestResourceNames().Where(n => n.StartsWith(BuiltInPrefix, StringComparison.Ordinal) && n.EndsWith(".xshd", StringComparison.Ordinal)))
        {
            Register(() => avalon.GetManifestResourceStream(resource)!);
        }

        // Replaces AvalonEdit's basic "MarkDown" mode (names compare case-insensitively).
        _sources.Remove("MarkDown");
        var own = typeof(HighlightingRegistry).Assembly;
        Register(() => own.GetManifestResourceStream(MarkdownResource)!);
    }

    /// <summary>Language names for the language picker, plain text first.</summary>
    public IReadOnlyList<string> Names => [PlainText, .. _sources.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public string LanguageForFile(string? path)
    {
        var extension = Path.GetExtension(path ?? "");
        return _byExtension.TryGetValue(extension, out var name) ? name : PlainText;
    }

    public IHighlightingDefinition? Get(string language, bool dark)
    {
        if (!_sources.ContainsKey(language))
        {
            return null;
        }

        if (!_loaded.TryGetValue((language, dark), out var definition))
        {
            try
            {
                definition = Load(language, dark);
            }
            catch (Exception ex) when (ex is HighlightingDefinitionInvalidException or XmlException or ArgumentException)
            {
                Services.ErrorLog.Write(ex, $"Loading highlighting for {language}");
                return null;
            }

            _loaded[(language, dark)] = definition;
        }

        return definition;
    }

    private void Register(Func<Stream> open)
    {
        var xshd = Read(open);
        _sources[xshd.Name] = open;
        foreach (var extension in xshd.Extensions)
        {
            _byExtension[extension] = xshd.Name;
        }
    }

    private IHighlightingDefinition Load(string language, bool dark)
    {
        var xshd = Read(_sources[language]);
        if (dark)
        {
            xshd.AcceptElements(new DarkenVisitor());
        }

        return HighlightingLoader.Load(xshd, new Resolver(this, dark));
    }

    private static XshdSyntaxDefinition Read(Func<Stream> open)
    {
        using var stream = open();
        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.LoadXshd(reader);
    }

    private sealed class Resolver(HighlightingRegistry registry, bool dark) : IHighlightingDefinitionReferenceResolver
    {
        public IHighlightingDefinition? GetDefinition(string name) => registry.Get(name, dark);
    }

    /// <summary>Rewrites every color in a definition so it reads well on a dark background.</summary>
    private sealed class DarkenVisitor : IXshdVisitor
    {
        public object? VisitRuleSet(XshdRuleSet ruleSet)
        {
            ruleSet.AcceptElements(this);
            return null;
        }

        public object? VisitColor(XshdColor color)
        {
            color.Foreground = Adapt(color.Foreground);
            color.Background = color.Background is null ? null : new SimpleHighlightingBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
            return null;
        }

        public object? VisitKeywords(XshdKeywords keywords)
        {
            Visit(keywords.ColorReference);
            return null;
        }

        public object? VisitSpan(XshdSpan span)
        {
            Visit(span.SpanColorReference);
            Visit(span.BeginColorReference);
            Visit(span.EndColorReference);
            span.RuleSetReference.InlineElement?.AcceptVisitor(this);
            return null;
        }

        public object? VisitImport(XshdImport import) => null;

        public object? VisitRule(XshdRule rule)
        {
            Visit(rule.ColorReference);
            return null;
        }

        private void Visit(XshdReference<XshdColor> reference) => reference.InlineElement?.AcceptVisitor(this);

        private static HighlightingBrush? Adapt(HighlightingBrush? brush) =>
            brush?.GetColor(null) is { } color ? new SimpleHighlightingBrush(Lighten(color)) : brush;
    }

    /// <summary>
    /// Keeps the hue and moves dark colors into the 0.6–0.75 lightness band typical of dark editor themes,
    /// so they stay distinguishable from the default text color; already-light colors are kept.
    /// </summary>
    public static Color Lighten(Color color)
    {
        var (h, s, l) = ToHsl(color);
        if (l >= 0.6)
        {
            return color;
        }

        l = Math.Clamp(0.62 + (0.5 - l) * 0.25, 0.6, 0.75);
        s = Math.Min(s, 0.8);
        return FromHsl(h, s, l, color.A);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2;
        if (max != min)
        {
            var d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }

        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l, byte alpha)
    {
        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3);
            g = Hue(p, q, h);
            b = Hue(p, q, h - 1.0 / 3);
        }

        return Color.FromArgb(alpha, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
    }
}
