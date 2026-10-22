using System.Runtime.InteropServices;

namespace WinUse.Core.Java;

/// <summary>
/// Raw layout of <c>WindowsAccessBridge-64.dll</c> (OpenJDK <c>AccessBridgeCalls.h</c> / <c>AccessBridgePackages.h</c>).
/// <c>vmID</c> is a C <c>long</c> (32-bit), <c>JOBJECT64</c> is a <c>jlong</c> (64-bit), <c>BOOL</c> is a 32-bit int.
/// The DLL path is only known at run time, so the exports are bound through <see cref="NativeLibrary"/> rather than
/// <c>DllImport</c>.
/// </summary>
internal static unsafe class JabNative
{
    public const int MaxStringSize = 1024;
    public const int ShortStringSize = 256;
    public const int MaxActionInfo = 256;
    public const int MaxActionsToDo = 32;
    public const int AccessibleValueInterface = 1;   // cAccessibleValueInterface bit of accessibleInterfaces

    [StructLayout(LayoutKind.Sequential)]
    public struct AccessibleContextInfo
    {
        public fixed char name[MaxStringSize];
        public fixed char description[MaxStringSize];
        public fixed char role[ShortStringSize];
        public fixed char role_en_US[ShortStringSize];
        public fixed char states[ShortStringSize];
        public fixed char states_en_US[ShortStringSize];
        public int indexInParent;
        public int childrenCount;
        public int x, y, width, height;
        public int accessibleComponent, accessibleAction, accessibleSelection, accessibleText;
        public int accessibleInterfaces;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AccessibleTextInfo
    {
        public int charCount, caretIndex, indexAtPoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AccessibleActionInfo
    {
        public fixed char name[ShortStringSize];
    }

    /// <summary>131,076 bytes: allocate on the native heap, never on the stack.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AccessibleActions
    {
        public int actionsCount;
        public AccessibleActionInfo actionInfo; // first of MaxActionInfo consecutive entries
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AccessibleActionsToDo
    {
        public int actionsCount;
        public AccessibleActionInfo action0; // first of MaxActionsToDo consecutive entries; only one is used
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AccessBridgeVersionInfo
    {
        public fixed char VMversion[ShortStringSize];
        public fixed char bridgeJavaClassVersion[ShortStringSize];
        public fixed char bridgeJavaDLLVersion[ShortStringSize];
        public fixed char bridgeWinDLLVersion[ShortStringSize];
    }

    public static int SizeOfActions => sizeof(int) + MaxActionInfo * sizeof(AccessibleActionInfo);
    public static int SizeOfActionsToDo => sizeof(int) + MaxActionsToDo * sizeof(AccessibleActionInfo);

    /// <summary>Decodes a NUL-terminated fixed-size <c>wchar_t</c> buffer.</summary>
    public static string Str(char* p, int capacity)
    {
        var n = 0;
        while (n < capacity && p[n] != '\0') n++;
        return new string(p, 0, n);
    }

    /// <summary>The bound exports. Created once per process by <see cref="Load"/>.</summary>
    public sealed class Functions
    {
        public nint Library;
        public delegate* unmanaged<void> Windows_run;
        public delegate* unmanaged<nint, int> isJavaWindow;
        public delegate* unmanaged<nint, int*, long*, int> getAccessibleContextFromHWND;
        public delegate* unmanaged<int, long, nint> getHWNDFromAccessibleContext;
        public delegate* unmanaged<int, long, AccessibleContextInfo*, int> getAccessibleContextInfo;
        public delegate* unmanaged<int, long, int, long> getAccessibleChildFromContext;
        public delegate* unmanaged<int, long, long> getAccessibleParentFromContext;
        public delegate* unmanaged<int, long, void> releaseJavaObject;
        public delegate* unmanaged<int, AccessBridgeVersionInfo*, int> getVersionInfo;
        public delegate* unmanaged<int, long, AccessibleActions*, int> getAccessibleActions;
        public delegate* unmanaged<int, long, AccessibleActionsToDo*, int*, int> doAccessibleActions;
        public delegate* unmanaged<int, long, AccessibleTextInfo*, int, int, int> getAccessibleTextInfo;
        public delegate* unmanaged<int, long, int, int, char*, short, int> getAccessibleTextRange;
        public delegate* unmanaged<int, long, char*, short, int> getCurrentAccessibleValueFromContext;
        public delegate* unmanaged<int, long, char*, int> setTextContents;
        public delegate* unmanaged<int, long, int> requestFocus;
        public delegate* unmanaged<int, long, char*, int, int> getVirtualAccessibleName;
        public delegate* unmanaged<int, long, int, void> addAccessibleSelectionFromContext;
        public delegate* unmanaged<int, long, void> clearAccessibleSelectionFromContext;
        public delegate* unmanaged<int, long, int, long> getAccessibleSelectionFromContext;
        public delegate* unmanaged<int, long, int> getAccessibleSelectionCountFromContext;
    }

    /// <summary>Loads the DLL and binds every export winuse uses. Throws when the file or an export is missing.</summary>
    public static Functions Load(string path)
    {
        var lib = NativeLibrary.Load(path);
        try
        {
            void* E(string name) => (void*)NativeLibrary.GetExport(lib, name);
            return new Functions
            {
                Library = lib,
                Windows_run = (delegate* unmanaged<void>)E("Windows_run"),
                isJavaWindow = (delegate* unmanaged<nint, int>)E("isJavaWindow"),
                getAccessibleContextFromHWND = (delegate* unmanaged<nint, int*, long*, int>)E("getAccessibleContextFromHWND"),
                getHWNDFromAccessibleContext = (delegate* unmanaged<int, long, nint>)E("getHWNDFromAccessibleContext"),
                getAccessibleContextInfo = (delegate* unmanaged<int, long, AccessibleContextInfo*, int>)E("getAccessibleContextInfo"),
                getAccessibleChildFromContext = (delegate* unmanaged<int, long, int, long>)E("getAccessibleChildFromContext"),
                getAccessibleParentFromContext = (delegate* unmanaged<int, long, long>)E("getAccessibleParentFromContext"),
                releaseJavaObject = (delegate* unmanaged<int, long, void>)E("releaseJavaObject"),
                getVersionInfo = (delegate* unmanaged<int, AccessBridgeVersionInfo*, int>)E("getVersionInfo"),
                getAccessibleActions = (delegate* unmanaged<int, long, AccessibleActions*, int>)E("getAccessibleActions"),
                doAccessibleActions = (delegate* unmanaged<int, long, AccessibleActionsToDo*, int*, int>)E("doAccessibleActions"),
                getAccessibleTextInfo = (delegate* unmanaged<int, long, AccessibleTextInfo*, int, int, int>)E("getAccessibleTextInfo"),
                getAccessibleTextRange = (delegate* unmanaged<int, long, int, int, char*, short, int>)E("getAccessibleTextRange"),
                getCurrentAccessibleValueFromContext = (delegate* unmanaged<int, long, char*, short, int>)E("getCurrentAccessibleValueFromContext"),
                setTextContents = (delegate* unmanaged<int, long, char*, int>)E("setTextContents"),
                requestFocus = (delegate* unmanaged<int, long, int>)E("requestFocus"),
                getVirtualAccessibleName = (delegate* unmanaged<int, long, char*, int, int>)E("getVirtualAccessibleName"),
                addAccessibleSelectionFromContext = (delegate* unmanaged<int, long, int, void>)E("addAccessibleSelectionFromContext"),
                clearAccessibleSelectionFromContext = (delegate* unmanaged<int, long, void>)E("clearAccessibleSelectionFromContext"),
                getAccessibleSelectionFromContext = (delegate* unmanaged<int, long, int, long>)E("getAccessibleSelectionFromContext"),
                getAccessibleSelectionCountFromContext = (delegate* unmanaged<int, long, int>)E("getAccessibleSelectionCountFromContext"),
            };
        }
        catch
        {
            NativeLibrary.Free(lib);
            throw;
        }
    }
}
