namespace WinUse.Core.Uia;

public static class InteractiveFilter
{
    private static readonly HashSet<string> Interactive = new(StringComparer.Ordinal)
    {
        "Button", "Edit", "ComboBox", "CheckBox", "RadioButton", "MenuItem", "TabItem", "ListItem",
        "TreeItem", "Hyperlink", "Document", "SplitButton", "Slider", "Spinner", "Tab", "List", "Tree",
        "Menu", "MenuBar", "ToolBar", "Window",
    };

    public static bool IsInteractive(string controlType) => Interactive.Contains(controlType);
}
