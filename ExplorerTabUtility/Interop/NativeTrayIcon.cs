using System;
using System.Runtime.InteropServices;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;

namespace ExplorerTabUtility.Interop;

public enum TrayCommand : uint { Settings = 1, WindowHook, ReuseTabs, KeyboardHook, MouseHook, Startup, Updates, Exit }

// Shell notification icon and a standard Win32 popup menu with checkmarks.
public sealed class NativeTrayIcon : IDisposable
{
    private const string WindowClass = "ExplorerTabUtility.TrayWindow";
    private const uint CallbackMessage = 0x8001;
    private const uint ShowMenuMessage = 0x8003;
    private readonly WindowProc _procedure;
    private readonly Action<TrayCommand> _command;
    private readonly Action _shutdown;
    private readonly nint _window;
    private readonly nint _icon;
    private readonly uint _taskbarCreated;
    private bool _visible;
    private bool _disposed;
    private bool _menuQueued;
    private bool _menuOpen;
    private System.Drawing.Point _menuPoint;

    public NativeTrayIcon(string iconPath, Action<TrayCommand> command, Action shutdown)
    {
        _command = command;
        _shutdown = shutdown;
        _procedure = ProcessMessage;
        var instance = GetModuleHandle(null);
        var cls = new WindowClassData { Instance = instance, Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), ClassName = WindowClass };
        RegisterClass(ref cls);
        _window = CreateWindowEx(0x80, WindowClass, WindowClass, 0x80000000, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _icon = LoadImage(0, iconPath, 1, 0, 0, 0x10 | 0x40);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        SetVisible(!SettingsManager.IsTrayIconHidden);
    }

    public static void ShowExistingSettings()
        => SendInstanceCommand(TrayCommand.Settings);
    public static void SendInstanceCommand(TrayCommand command)
    {
        var window = FindWindow(WindowClass, WindowClass);
        if (window == 0) return;
        GetWindowThreadProcessId(window, out var processId);
        AllowSetForegroundWindow(processId);
        PostMessage(window, 0x8002, (nuint)command, 0);
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1,
        Flags = 1 | 2 | 4 | 0x80, Callback = CallbackMessage, Icon = _icon, Tip = "Explorer Tab Utility",
        Info = "", InfoTitle = ""
    };
    public void SetVisible(bool visible)
    {
        if (_disposed) return;
        if (_visible == visible) return;
        _visible = visible;
        var data = Data();
        ShellNotifyIcon(visible ? 0u : 2u, ref data);
        if (visible) { data.Version = 4; ShellNotifyIcon(4, ref data); }
    }
    public void NotifyUpdate(string version)
    {
        if (!_visible || _disposed) return;
        var data = Data();
        data.Flags = 0x10;
        data.InfoTitle = "Explorer Tab Utility update";
        data.Info = $"Version {version} is available. Click to update and restart.";
        data.InfoFlags = 1 | 0x80;
        ShellNotifyIcon(1, ref data);
    }
    private nint ProcessMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x11) return 1; // WM_QUERYENDSESSION
        if (message == 0x16 && wParam != 0) { _shutdown(); return 0; } // WM_ENDSESSION
        if (message == _taskbarCreated && _visible)
        {
            _visible = false;
            SetVisible(true);
            return 0;
        }
        if (message == 0x8002)
        {
            var command = wParam == 0 ? TrayCommand.Settings : (TrayCommand)wParam;
            if (Enum.IsDefined(command)) _command(command);
            return 0;
        }
        if (message == ShowMenuMessage)
        {
            _menuQueued = false;
            if (!_disposed && !_menuOpen) ShowMenu();
            return 0;
        }
        if (message == CallbackMessage)
        {
            var action = (uint)((long)lParam & 0xffff);
            // Version 4 uses WM_CONTEXTMENU and NIN_SELECT, not the legacy
            // mouse-up messages. Return to the Shell before entering a menu loop.
            if (action == 0x7b && !_menuQueued && !_menuOpen)
            {
                _menuPoint = new(unchecked((short)((long)wParam & 0xffff)), unchecked((short)(((long)wParam >> 16) & 0xffff)));
                if (_menuPoint.X == -1 && _menuPoint.Y == -1) GetCursorPos(out _menuPoint);
                _menuQueued = true;
                PostMessage(_window, ShowMenuMessage, 0, 0);
            }
            else if (action is 0x400 or 0x401 or 0x405) _command(TrayCommand.Settings);
            return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }
    private void ShowMenu()
    {
        _menuOpen = true;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var menu = CreatePopupMenu();
        void Item(TrayCommand id, string text, bool check = false) => AppendMenu(menu, check ? 8u : 0u, (nuint)id, text);
        void Separator() => AppendMenu(menu, 0x800, 0, null);
        Item(TrayCommand.Settings, "Settings");
        SetMenuDefaultItem(menu, (uint)TrayCommand.Settings, false);
        Separator();
        Item(TrayCommand.WindowHook, "Window hook", SettingsManager.IsWindowHookActive);
        Item(TrayCommand.ReuseTabs, "Reuse tabs", SettingsManager.ReuseTabs);
        Item(TrayCommand.KeyboardHook, "Keyboard shortcuts", SettingsManager.IsKeyboardHookActive);
        Item(TrayCommand.MouseHook, "Mouse shortcuts", SettingsManager.IsMouseHookActive);
        Separator();
        Item(TrayCommand.Startup, "Start with Windows", RegistryManager.IsStartupEnabled);
        Item(TrayCommand.Updates, "Check for updates");
        Separator();
        Item(TrayCommand.Exit, "Exit");
        WinAPI.WinApi.SetForegroundWindow(_window);
        uint selected;
        try { selected = TrackPopupMenu(menu, 0x100 | 2, _menuPoint.X, _menuPoint.Y, 0, _window, 0); }
        finally { DestroyMenu(menu); _menuOpen = false; PostMessage(_window, 0, 0, 0); }
        System.Diagnostics.Trace.WriteLine($"Tray menu closed after {elapsed.ElapsedMilliseconds} ms; command={selected}.");
        if (selected != 0) _command((TrayCommand)selected);
    }
    public void Dispose()
    {
        if (_disposed) return;
        SetVisible(false);
        _disposed = true;
        DestroyWindow(_window);
        if (_icon != 0) DestroyIcon(_icon);
        GC.KeepAlive(_procedure);
    }

    private delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassData
    {
        public uint Style; public nint Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClassData cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int x, int y, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string cls, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(nint menu, uint item, bool byPosition);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point point);
}
