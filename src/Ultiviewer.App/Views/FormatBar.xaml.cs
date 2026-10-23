using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using ICSharpCode.AvalonEdit.Editing;

namespace Ultiviewer.App.Views;

/// <summary>
/// The "Basic Text" toolbar above Markdown documents. Buttons run the editor's routed commands against the
/// text area, so they behave exactly like the shortcuts and the Format menu.
/// </summary>
public partial class FormatBar : UserControl
{
    /// <summary>OneNote's standard font colors.</summary>
    public static IReadOnlyList<(string Name, string? Hex)> Colors { get; } =
    [
        ("Automatic", null), ("Red", "#D64545"), ("Orange", "#E07B39"), ("Gold", "#C9A227"), ("Green", "#3E9B4F"),
        ("Teal", "#1E9E9E"), ("Blue", "#3A7BD5"), ("Purple", "#7E57C2"), ("Gray", "#7A7A7A"),
    ];

    private bool _updating;

    public FormatBar(TextArea target)
    {
        InitializeComponent();
        foreach (var button in Panel.Children.OfType<Button>())
        {
            button.CommandTarget = target;
        }
    }

    /// <summary>Raised when a paragraph style is picked: 0 = normal text, 1–6 = heading level.</summary>
    public event Action<int>? StyleChosen;

    /// <summary>Raised when a font color is picked (null removes the color).</summary>
    public event Action<string?>? ColorChosen;

    /// <summary>Shows the caret line's style without applying anything.</summary>
    public void ShowStyle(int level)
    {
        _updating = true;
        StyleBox.SelectedIndex = Math.Clamp(level, 0, 6);
        _updating = false;
    }

    private void OnStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && StyleBox.SelectedIndex >= 0)
        {
            StyleChosen?.Invoke(StyleBox.SelectedIndex);
        }
    }

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ColorButton, Placement = PlacementMode.Bottom };
        foreach (var (name, hex) in Colors)
        {
            var item = new MenuItem
            {
                Header = name,
                Icon = hex is null ? null : new Rectangle { Width = 12, Height = 12, RadiusX = 2, RadiusY = 2, Fill = (Brush)new BrushConverter().ConvertFromString(hex)! },
            };
            item.Click += (_, _) =>
            {
                ColorSwatch.Background = hex is null ? Brushes.Transparent : (Brush)new BrushConverter().ConvertFromString(hex)!;
                ColorChosen?.Invoke(hex);
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }
}
