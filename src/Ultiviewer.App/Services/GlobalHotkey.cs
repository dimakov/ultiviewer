using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Ultiviewer.App.Services;

/// <summary>
/// A Windows-wide shortcut (RegisterHotKey) delivered to a window's message loop. Registration fails when
/// another program already owns the combination; the caller reports that.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int Id = 0x5556; // "UV"
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private Action? _pressed;
    private bool _registered;

    public GlobalHotkey(IntPtr windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle) ?? throw new ArgumentException("Not a WPF window handle.", nameof(windowHandle));
        _source.AddHook(WndProc);
    }

    /// <summary>Registers <paramref name="gesture"/> (for example "Win+Alt+N"); empty unregisters. Returns false when it can't be used.</summary>
    public bool Register(string? gesture, Action pressed)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return true;
        }

        if (!TryParse(gesture, out var modifiers, out var key))
        {
            return false;
        }

        _pressed = pressed;
        _registered = RegisterHotKey(_source.Handle, Id, modifiers | ModNoRepeat, key);
        return _registered;
    }

    /// <summary>Parses "Ctrl+Alt+Shift+Win+Key" into RegisterHotKey modifiers and a virtual key.</summary>
    public static bool TryParse(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false; // a global shortcut without modifiers would swallow a key everywhere
        }

        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win" or "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        try
        {
            if (new KeyConverter().ConvertFromInvariantString(parts[^1]) is not Key key || key == Key.None)
            {
                return false;
            }

            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            return virtualKey != 0;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, Id);
            _registered = false;
        }
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id)
        {
            handled = true;
            _pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
