using System.Runtime.InteropServices;
using EasyShare.Resources;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace EasyShare.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = 0x8000 + 120;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const int SmCxSmallIcon = 49;
    private const int SmCySmallIcon = 50;
    private const uint WmDestroy = 0x0002;
    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint MfString = 0x00000000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int OpenCommand = 1001;
    private const int ExitCommand = 1002;
    private static readonly UIntPtr SubclassId = new(1);

    private readonly IntPtr _hwnd;
    private readonly Action _exitRequested;
    private readonly SubclassProc _subclassProc;
    private IntPtr _iconHandle;
    private bool _ownsIconHandle;
    private bool _disposed;

    public TrayIconService(Window window, Action exitRequested)
    {
        _hwnd = WindowNative.GetWindowHandle(window);
        _exitRequested = exitRequested;
        _subclassProc = WindowSubclassProc;

        var iconPath = ResolveIconPath();
        var iconWidth = GetSystemMetrics(SmCxSmallIcon);
        var iconHeight = GetSystemMetrics(SmCySmallIcon);
        _iconHandle = iconPath is not null
            ? LoadImage(IntPtr.Zero, iconPath, ImageIcon, iconWidth, iconHeight, LrLoadFromFile)
            : IntPtr.Zero;
        _ownsIconHandle = _iconHandle != IntPtr.Zero;

        if (_iconHandle == IntPtr.Zero)
        {
            _iconHandle = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        }

        SetWindowSubclass(_hwnd, _subclassProc, SubclassId, UIntPtr.Zero);
        ShellNotifyIcon(NimAdd, CreateNotifyIconData());
    }

    public void Hide()
    {
        if (_disposed)
        {
            return;
        }

        ShowWindow(_hwnd, ShowWindowCommand.Hide);
    }

    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        ShowWindow(_hwnd, ShowWindowCommand.Show);
        ShowWindow(_hwnd, ShowWindowCommand.Restore);
        SetForegroundWindow(_hwnd);
    }

    private NotifyIconData CreateNotifyIconData() =>
        new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _iconHandle,
            szTip = AppText.Get("AppName"),
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == TrayCallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (mouseMessage is WmLButtonDblClk)
            {
                Show();
            }
            else if (mouseMessage is WmRButtonUp or WmContextMenu)
            {
                ShowContextMenu();
            }

            return IntPtr.Zero;
        }

        if (message == WmDestroy)
        {
            Dispose();
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (_disposed)
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, new UIntPtr(OpenCommand), AppText.Get("TrayOpen"));
            AppendMenu(menu, MfString, new UIntPtr(ExitCommand), AppText.Get("TrayExit"));
            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmReturnCmd,
                point.X,
                point.Y,
                _hwnd,
                IntPtr.Zero);
            PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (command == OpenCommand)
            {
                Show();
            }
            else if (command == ExitCommand)
            {
                Exit();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Exit()
    {
        _exitRequested();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
        ShellNotifyIcon(NimDelete, CreateNotifyIconData());
        if (_ownsIconHandle && _iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
        }

        _iconHandle = IntPtr.Zero;
        _ownsIconHandle = false;
    }

    private static string? ResolveIconPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            Path.Combine(AppContext.BaseDirectory, "AppIcon.ico")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, ShowWindowCommand nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr hinst,
        string lpszName,
        uint uType,
        int cxDesired,
        int cyDesired,
        uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    private static bool ShellNotifyIcon(uint message, NotifyIconData data) =>
        Shell_NotifyIcon(message, ref data);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(
        IntPtr hmenu,
        uint fuFlags,
        int x,
        int y,
        IntPtr hwnd,
        IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint uMsg,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private enum ShowWindowCommand
    {
        Hide = 0,
        Show = 5,
        Restore = 9
    }
}
