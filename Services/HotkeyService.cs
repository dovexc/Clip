using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipperApp.Services;

public class HotkeyService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9001;

    private HwndSource? _source;
    private Action? _onHotkey;
    private bool _registered;

    public bool Register(Window window, uint modifiers, uint vk, Action onHotkey)
    {
        Unregister();
        _onHotkey = onHotkey;

        var helper = new WindowInteropHelper(window);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source.AddHook(WndProc);

        _registered = RegisterHotKey(helper.Handle, HOTKEY_ID, modifiers, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_source != null && _registered)
        {
            UnregisterHotKey(_source.Handle, HOTKEY_ID);
            _source.RemoveHook(WndProc);
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            _onHotkey?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Unregister();
}
