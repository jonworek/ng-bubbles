using System.Runtime.InteropServices;

namespace NgBubbles.Windows;

public sealed class TrayIcon : IDisposable
{
    private const uint Callback = 0x8001;
    private readonly nint window;
    private readonly SubclassProc procedure;
    private readonly Action show;
    private readonly Action quit;
    private readonly Action sync;
    private readonly Action checkUpdates;
    private readonly nint normal;
    private readonly nint unread;
    private readonly uint taskbarCreated;
    private int unreadCount = -1;
    private readonly Guid identity = new("7abff683-1811-45d5-a59e-f9e10bb89527");
    public TrayIcon(nint window, Action show, Action sync, Action quit, Action checkUpdates)
    {
        this.window = window; this.show = show; this.sync = sync; this.quit = quit; this.checkUpdates = checkUpdates;
        normal = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"), 1, 0, 0, 0x10);
        unread = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "icon-unread.ico"), 1, 0, 0, 0x10);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        procedure = Handle;
        if (!SetWindowSubclass(window, procedure, 1, 0)) throw new InvalidOperationException("Cannot create tray message handler.");
        Update(0, true);
    }
    public void Update(int count, bool add = false)
    {
        count = Math.Max(0, count);
        if (!add && unreadCount == count) return;
        unreadCount = count;
        var data = Data();
        if (!Shell_NotifyIcon(add ? 0u : 1u, ref data) && add) throw new InvalidOperationException("Cannot create tray icon.");
        if (add) { data.Version = 4; Shell_NotifyIcon(4, ref data); }
    }
    private NotifyData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = window, Id = 1, Flags = 1 | 2 | 4 | 0x20 | 0x80, Callback = Callback,
        Icon = unreadCount > 0 ? unread : normal, Tip = unreadCount > 0 ? $"NG Bubbles · {unreadCount} unread" : "NG Bubbles · No unread messages", Info = "", Title = "", Guid = identity };
    internal void SmokeTest()
    {
        foreach (var count in new[] { 0, 1, 54, 0 })
        {
            Update(count);
            var data = Data();
            var expected = count > 0 ? $"NG Bubbles · {count} unread" : "NG Bubbles · No unread messages";
            if ((data.Flags & 0x84) != 0x84 || data.Tip != expected || data.Icon != (count > 0 ? unread : normal))
                throw new InvalidOperationException("Tray icon does not enable its standard unread tooltip or matching icon.");
        }
        Update(54);
        var removed = Data();
        if (!Shell_NotifyIcon(2, ref removed)) throw new InvalidOperationException("Cannot simulate tray icon recreation.");
        SendMessage(window, taskbarCreated, 0, 0);
        var restored = Data();
        if (!Shell_NotifyIcon(1, ref restored) || restored.Tip != "NG Bubbles · 54 unread" || unreadCount != 54)
            throw new InvalidOperationException("Tray recreation lost the unread count.");
    }
    private nint Handle(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nint reference)
    {
        if (message == taskbarCreated) { try { Update(unreadCount, true); } catch { } }
        if (message == Callback)
        {
            var action = (uint)((long)lParam & 0xffff);
            if (action is 0x400 or 0x401 or 0x202) show();
            else if (action is 0x205 or 0x7b)
            {
                var menu = CreatePopupMenu();
                AppendMenu(menu, 0, 1, "Open NG Bubbles"); AppendMenu(menu, 0, 2, "Reconnect / sync"); AppendMenu(menu, 0, 4, "Check for updates"); AppendMenu(menu, 0x800, 0, ""); AppendMenu(menu, 0, 3, "Quit");
                GetCursorPos(out var point); SetForegroundWindow(window);
                var command = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, window, 0);
                DestroyMenu(menu);
                if (command == 1) show(); else if (command == 2) sync(); else if (command == 3) quit(); else if (command == 4) checkUpdates();
            }
            return 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    public void Dispose()
    {
        var data = Data(); Shell_NotifyIcon(2, ref data); RemoveWindowSubclass(window, procedure, 1);
        if (normal != 0) DestroyIcon(normal); if (unread != 0) DestroyIcon(unread);
        GC.KeepAlive(procedure);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size; public nint Window; public uint Id; public uint Flags; public uint Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nint reference);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint action, ref NotifyData data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nint reference);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
