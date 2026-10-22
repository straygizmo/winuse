using System.Runtime.InteropServices;

namespace WinUse.Core.Windows;

/// <summary>Win32 pieces of the built-in Java Access Bridge client: a hidden top-level window and the bridge's messages.</summary>
internal static partial class NativeMethods
{
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_COPYDATA = 0x004A;
    public const uint WM_USER = 0x0400;
    /// <summary>Request waiting in the memory-mapped file; <c>wParam</c> = sender window, <c>lParam</c> = byte count.</summary>
    public const uint AB_MESSAGE_WAITING = WM_USER + 0x1001;
    /// <summary>One side is going away; <c>wParam</c> = its window.</summary>
    public const uint AB_DLL_GOING_AWAY = WM_USER + 0x1003;
    public const nint HWND_BROADCAST = 0xFFFF;
    public const uint SMTO_BLOCK = 0x0001, SMTO_NOTIMEOUTIFNOTHUNG = 0x0008;
    public const uint WS_POPUP = 0x80000000;
    public const string FromWindowsHelloMessage = "AccessBridge-FromWindows-Hello";
    public const string FromJavaHelloMessage = "AccessBridge-FromJava-Hello";

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        public nuint dwData;
        public uint cbData;
        public nint lpData;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetParent(nint hwnd);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? moduleName);
}
