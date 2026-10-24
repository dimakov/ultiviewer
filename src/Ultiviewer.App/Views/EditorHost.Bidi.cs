using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using Ultiviewer.App.Editor;
using Ultiviewer.Core.Text;

namespace Ultiviewer.App.Views;

/// <summary>
/// Right-to-left documents (Hebrew, Arabic): the editor's direction follows the document — from its text, or chosen
/// with Ctrl+Alt+R / Ctrl+Alt+L, or Ctrl+Right Shift / Ctrl+Left Shift as in Windows Notepad and Word. Lines written
/// the other way keep their own word order (<see cref="BidiLineGenerator"/>), and the arrow keys move the way such a
/// line reads.
/// </summary>
public partial class EditorHost
{
    private readonly BidiLineGenerator _bidi = new();
    private Key _directionShift = Key.None;

    private void InitializeBidi()
    {
        Editor.TextArea.TextView.ElementGenerators.Insert(0, _bidi);
        Editor.TextArea.PreviewKeyDown += OnBidiPreviewKeyDown;
        Editor.TextArea.PreviewKeyUp += OnBidiPreviewKeyUp;
        Bind(EditorCommands.RightToLeft, (_, _) => _vm?.SetDirection(_current, TextDirectionMode.RightToLeft));
        Bind(EditorCommands.LeftToRight, (_, _) => _vm?.SetDirection(_current, TextDirectionMode.LeftToRight));
        Bind(EditorCommands.AutomaticDirection, (_, _) => _vm?.SetDirection(_current, TextDirectionMode.Auto));
    }

    private void ApplyDirection()
    {
        var rightToLeft = _current?.IsRightToLeft == true;
        _bidi.RightToLeft = rightToLeft;
        var direction = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (Editor.FlowDirection != direction)
        {
            Editor.FlowDirection = direction; // AvalonEdit lays the lines out again
        }
    }

    private void OnBidiPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftShift or Key.RightShift && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            _directionShift = key;
            return;
        }

        if (key is not (Key.LeftCtrl or Key.RightCtrl))
        {
            _directionShift = Key.None; // Ctrl+Shift+V and other chords don't change the direction
        }

        if (key is Key.Left or Key.Right && LineReadsTheOtherWay())
        {
            // AvalonEdit moves by the editor's direction; this line reads the other way, so the keys swap.
            var right = key == Key.Left;
            RoutedCommand? command = Keyboard.Modifiers switch
            {
                ModifierKeys.None => right ? EditingCommands.MoveRightByCharacter : EditingCommands.MoveLeftByCharacter,
                ModifierKeys.Control => right ? EditingCommands.MoveRightByWord : EditingCommands.MoveLeftByWord,
                ModifierKeys.Shift => right ? EditingCommands.SelectRightByCharacter : EditingCommands.SelectLeftByCharacter,
                ModifierKeys.Control | ModifierKeys.Shift => right ? EditingCommands.SelectRightByWord : EditingCommands.SelectLeftByWord,
                _ => null,
            };
            if (command is not null)
            {
                command.Execute(null, Editor.TextArea);
                e.Handled = true;
            }
        }
    }

    private void OnBidiPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift && e.Key == _directionShift && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _current is not null)
        {
            _vm?.SetDirection(_current, e.Key == Key.RightShift ? TextDirectionMode.RightToLeft : TextDirectionMode.LeftToRight);
        }

        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            _directionShift = Key.None;
        }
    }

    /// <summary>The caret's line is written in the other direction than the editor's.</summary>
    private bool LineReadsTheOtherWay()
    {
        var line = Editor.Document.GetLineByNumber(Editor.TextArea.Caret.Line);
        return BidiText.LineDirection(Editor.Document.GetText(line)) is { } rightToLeft && rightToLeft != _bidi.RightToLeft;
    }
}
