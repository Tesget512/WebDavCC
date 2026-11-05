using System.Runtime.InteropServices;

namespace WebDavShare.Core;

/// <summary>
/// 系统托盘图标。WinUI 3 没有内置托盘 API，用 Shell_NotifyIcon 实现。
/// 需要一个隐藏的消息窗口来接收鼠标事件。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 1;
    private const int WM_DESTROY = 0x0002;
    private const int WM_COMMAND = 0x0111;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const int TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;
    private const int MF_STRING = 0, MF_SEPARATOR = 0x800, MF_GRAYED = 1;

    private readonly WndProcDelegate _wndProc;   // 必须持有，否则会被 GC 回收导致崩溃
    private readonly List<(int Id, string Text, Action? Handler, bool Enabled)> _menu = [];
    private readonly uint _taskbarCreatedMsg;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private string _tip = "";
    private bool _added;
    private bool _disposed;

    public event Action? DoubleClicked;

    public TrayIcon(string iconPath, string tip)
    {
        _wndProc = WndProc;
        _tip = tip;
        // explorer 崩溃重启后任务栏重建，旧图标全部消失；广播此消息时重新注册
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        CreateMessageWindow();
        LoadIcon(iconPath);
    }

    public void AddMenuItem(string text, Action handler, bool enabled = true) =>
        _menu.Add((_menu.Count + 1, text, handler, enabled));

    public void AddSeparator() => _menu.Add((0, "-", null, true));

    public void ClearMenu() => _menu.Clear();

    private void CreateMessageWindow()
    {
        var className = "WebDavShareTray_" + Guid.NewGuid().ToString("N")[..8];
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClass(ref wc);
        // HWND_MESSAGE 窗口不可见、不占任务栏，专用于接收消息
        _hwnd = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private void LoadIcon(string iconPath)
    {
        if (File.Exists(iconPath))
        {
            _icon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x00000010);  // LR_LOADFROMFILE
        }
        if (_icon == IntPtr.Zero)
        {
            _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));                // IDI_APPLICATION
        }
    }

    public bool Show()
    {
        var data = BuildData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        var ok = Shell_NotifyIcon(_added ? NIM_MODIFY : NIM_ADD, ref data);
        if (ok)
        {
            _added = true;
        }
        return ok;
    }

    public void UpdateTip(string tip)
    {
        _tip = tip;
        if (_added)
        {
            var data = BuildData(NIF_TIP);
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_added)
        {
            return;
        }
        var data = BuildData(NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = message;
        data.dwInfoFlags = 1;   // NIIF_INFO
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA BuildData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = _tip.Length > 127 ? _tip[..127] : _tip,
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_TRAY:
                var mouseMessage = (int)lParam;
                if (mouseMessage == WM_RBUTTONUP)
                {
                    ShowContextMenu();
                }
                else if (mouseMessage == WM_LBUTTONUP)
                {
                    DoubleClicked?.Invoke();
                }
                return IntPtr.Zero;

            case WM_DESTROY:
                return IntPtr.Zero;
        }
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            _added = false;
            Show();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }
        try
        {
            foreach (var (id, text, _, enabled) in _menu)
            {
                if (id == 0)
                {
                    AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
                }
                else
                {
                    AppendMenu(menu, MF_STRING | (enabled ? 0 : MF_GRAYED), new IntPtr(id), text);
                }
            }

            GetCursorPos(out var point);
            // 不调 SetForegroundWindow 的话，菜单会在点击别处时卡住不消失
            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                point.X, point.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);

            if (chosen > 0)
            {
                _menu.FirstOrDefault(m => m.Id == chosen).Handler?.Invoke();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_added)
        {
            var data = BuildData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    // ---------------- P/Invoke ----------------

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(
        IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, int flags, IntPtr id, string? item);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(
        IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}
