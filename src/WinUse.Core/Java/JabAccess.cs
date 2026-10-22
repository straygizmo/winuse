using System.Runtime.InteropServices;
using static WinUse.Core.Java.JabNative;

namespace WinUse.Core.Java;

/// <summary><see cref="IJabAccess"/> over the bound <c>WindowsAccessBridge-64.dll</c> exports. Dispatcher thread only.</summary>
internal sealed unsafe class JabAccess(Functions f) : IJabAccess
{
    public bool IsJavaWindow(nint hwnd) => f.isJavaWindow(hwnd) != 0;

    public JabHandle? GetContextFromHwnd(nint hwnd)
    {
        int vm = 0;
        long ac = 0;
        if (f.getAccessibleContextFromHWND(hwnd, &vm, &ac) == 0 || ac == 0) return null;
        return new JabHandle(vm, ac);
    }

    public JabContextInfo? GetInfo(JabHandle h)
    {
        var info = new AccessibleContextInfo();
        if (f.getAccessibleContextInfo(h.VmId, h.Ac, &info) == 0) return null;
        return new JabContextInfo(
            NullIfEmpty(Str(info.name, MaxStringSize)),
            NullIfEmpty(Str(info.description, MaxStringSize)),
            Str(info.role_en_US, ShortStringSize),
            Str(info.states_en_US, ShortStringSize),
            info.indexInParent, info.childrenCount, info.x, info.y, info.width, info.height,
            info.accessibleAction != 0, info.accessibleSelection != 0, info.accessibleText != 0,
            (info.accessibleInterfaces & AccessibleValueInterface) != 0);
    }

    public JabHandle? GetChild(JabHandle h, int index)
    {
        var ac = f.getAccessibleChildFromContext(h.VmId, h.Ac, index);
        return ac == 0 ? null : new JabHandle(h.VmId, ac);
    }

    public void Release(JabHandle h) => f.releaseJavaObject(h.VmId, h.Ac);

    public IReadOnlyList<string> GetActions(JabHandle h)
    {
        var p = (AccessibleActions*)NativeMemory.AllocZeroed((nuint)SizeOfActions);
        try
        {
            if (f.getAccessibleActions(h.VmId, h.Ac, p) == 0) return [];
            var n = Math.Clamp(p->actionsCount, 0, MaxActionInfo);
            var list = new string[n];
            var first = (AccessibleActionInfo*)&p->actionInfo;
            for (var i = 0; i < n; i++) list[i] = Str(first[i].name, ShortStringSize);
            return list;
        }
        finally
        {
            NativeMemory.Free(p);
        }
    }

    public bool DoAction(JabHandle h, string action)
    {
        var p = (AccessibleActionsToDo*)NativeMemory.AllocZeroed((nuint)SizeOfActionsToDo);
        try
        {
            p->actionsCount = 1;
            var dst = p->action0.name;
            var n = Math.Min(action.Length, ShortStringSize - 1);
            for (var i = 0; i < n; i++) dst[i] = action[i];
            int failure = -1;
            return f.doAccessibleActions(h.VmId, h.Ac, p, &failure) != 0;
        }
        finally
        {
            NativeMemory.Free(p);
        }
    }

    public string? GetText(JabHandle h, int maxChars)
    {
        var ti = new AccessibleTextInfo();
        if (f.getAccessibleTextInfo(h.VmId, h.Ac, &ti, 0, 0) == 0) return null;
        var n = Math.Clamp(Math.Min(ti.charCount, maxChars), 0, short.MaxValue - 1);
        if (n == 0) return "";
        var buf = new char[n + 1];
        fixed (char* p = buf)
        {
            if (f.getAccessibleTextRange(h.VmId, h.Ac, 0, n - 1, p, (short)(n + 1)) == 0) return null;
            return Str(p, n + 1);
        }
    }

    public string? GetValue(JabHandle h)
    {
        var buf = new char[MaxStringSize];
        fixed (char* p = buf)
        {
            if (f.getCurrentAccessibleValueFromContext(h.VmId, h.Ac, p, (short)MaxStringSize) == 0) return null;
            return NullIfEmpty(Str(p, MaxStringSize));
        }
    }

    public bool SetText(JabHandle h, string text)
    {
        fixed (char* p = text + '\0')
            return f.setTextContents(h.VmId, h.Ac, p) != 0;
    }

    public bool RequestFocus(JabHandle h) => f.requestFocus(h.VmId, h.Ac) != 0;

    public void AddSelection(JabHandle h, int index) => f.addAccessibleSelectionFromContext(h.VmId, h.Ac, index);

    public void ClearSelection(JabHandle h) => f.clearAccessibleSelectionFromContext(h.VmId, h.Ac);

    public JabHandle? GetSelection(JabHandle h, int index)
    {
        var ac = f.getAccessibleSelectionFromContext(h.VmId, h.Ac, index);
        return ac == 0 ? null : new JabHandle(h.VmId, ac);
    }

    public int GetSelectionCount(JabHandle h) => f.getAccessibleSelectionCountFromContext(h.VmId, h.Ac);

    public string? GetVirtualName(JabHandle h)
    {
        var buf = new char[MaxStringSize];
        fixed (char* p = buf)
        {
            if (f.getVirtualAccessibleName(h.VmId, h.Ac, p, MaxStringSize) == 0) return null;
            return NullIfEmpty(Str(p, MaxStringSize));
        }
    }

    public JabVersionInfo? GetVersion(int vmId)
    {
        var v = new AccessBridgeVersionInfo();
        if (f.getVersionInfo(vmId, &v) == 0) return null;
        return new JabVersionInfo(Str(v.VMversion, ShortStringSize), Str(v.bridgeJavaDLLVersion, ShortStringSize),
            Str(v.bridgeWinDLLVersion, ShortStringSize));
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
