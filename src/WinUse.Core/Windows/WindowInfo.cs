namespace WinUse.Core.Windows;

public readonly record struct Rect(int X, int Y, int Width, int Height);

public enum WindowState { Normal, Minimized, Maximized }

public sealed record WindowInfo(nint Hwnd, string Title, string ClassName, string ProcessName, int Pid, Rect Rect, WindowState State);
