namespace WinUse.Core.Java;

/// <summary>The accessible states winuse reads from a JAB <c>states_en_US</c> list.</summary>
public readonly record struct JavaStateFlags(bool Enabled, bool Showing, bool Focused, bool Checked);

public static class JavaStates
{
    /// <summary>Parses the comma-separated <c>states_en_US</c> string ("enabled,focusable,visible,showing,...").</summary>
    public static JavaStateFlags Parse(string statesEnUs)
    {
        bool enabled = false, showing = false, focused = false, checkedState = false;
        foreach (var raw in statesEnUs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw)
            {
                case "enabled": enabled = true; break;
                case "showing": showing = true; break;
                case "focused": focused = true; break;
                case "checked": checkedState = true; break;
            }
        }
        return new JavaStateFlags(enabled, showing, focused, checkedState);
    }

    /// <summary>Three-valued checked state: only check boxes, radio buttons and toggle buttons have one.</summary>
    public static bool? CheckedOf(JavaStateFlags flags, string controlType, string role) =>
        controlType is "CheckBox" or "RadioButton" || role == "toggle button" ? flags.Checked : null;
}
