using WinUse.Core.Windows;

namespace WinUse.Core.Web;

public sealed record DomNode(
    string Ref,
    string Role,
    string? Name,
    string Tag,
    string? Value,
    bool Enabled,
    bool Focused,
    bool? Checked,
    Rect Rect,
    string Selector);

public sealed record PageInfo(string Id, string Url, string Title);
