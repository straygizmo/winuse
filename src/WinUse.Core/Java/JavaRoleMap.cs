namespace WinUse.Core.Java;

/// <summary>Maps Java Access Bridge roles (<c>role_en_US</c>) onto the UIA control type names the rest of winuse uses.</summary>
public static class JavaRoleMap
{
    private static readonly Dictionary<string, string> ByRole = new(StringComparer.Ordinal)
    {
        ["push button"] = "Button",
        ["toggle button"] = "Button",
        ["menu bar"] = "MenuBar",
        ["menu"] = "MenuItem",          // an openable menu such as "File"; UIA reports those as MenuItem too
        ["menu item"] = "MenuItem",
        ["popup menu"] = "Menu",
        ["check box"] = "CheckBox",
        ["radio button"] = "RadioButton",
        ["combo box"] = "ComboBox",
        ["text"] = "Edit",
        ["password text"] = "Edit",
        ["label"] = "Text",
        ["list"] = "List",
        ["tree"] = "Tree",
        ["table"] = "Table",
        ["page tab list"] = "Tab",
        ["page tab"] = "TabItem",
        ["tool bar"] = "ToolBar",
        ["slider"] = "Slider",
        ["spinbox"] = "Spinner",
        ["progress bar"] = "ProgressBar",
        ["scroll bar"] = "ScrollBar",
        ["hyperlink"] = "Hyperlink",
        ["frame"] = "Window",
        ["dialog"] = "Window",
        ["window"] = "Window",
        ["internal frame"] = "Window",
        ["panel"] = "Pane",
        ["root pane"] = "Pane",
        ["layered pane"] = "Pane",
        ["glass pane"] = "Pane",
        ["scroll pane"] = "Pane",
        ["viewport"] = "Pane",
        ["split pane"] = "Pane",
        ["desktop pane"] = "Pane",
        ["filler"] = "Pane",
        ["tool tip"] = "Pane",
        ["separator"] = "Pane",
        ["canvas"] = "Pane",
        ["icon"] = "Pane",
    };

    /// <summary>
    /// The control type for a JAB role. Children of a list / tab strip / tree are items whatever their own role says
    /// (Swing renders them with labels or reports them as "unknown").
    /// </summary>
    public static string ControlTypeOf(string role, string? parentRole)
    {
        switch (parentRole)
        {
            case "list" when role == "label": return "ListItem";
            case "page tab list" when role == "label": return "TabItem";
            case "tree" when role == "unknown": return "TreeItem";
        }
        return ByRole.TryGetValue(role, out var ct) ? ct : "Custom";
    }

    /// <summary>
    /// Menu bars, menus, menu items and the check / radio items inside a menu: kept in the tree even while the menu is
    /// closed, because they can be invoked without opening it.
    /// </summary>
    public static bool IsMenuFamily(string controlType, string? parentControlType) =>
        controlType is "MenuBar" or "Menu" or "MenuItem"
        || (controlType is "CheckBox" or "RadioButton" && parentControlType is "MenuItem" or "Menu");
}
