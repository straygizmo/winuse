using System.Text.Json.Serialization;

namespace WinUse.Core.Refs;

public enum RefKind { Window, Uia, Dom, Point }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WindowLocator), "window")]
[JsonDerivedType(typeof(UiaLocator), "uia")]
[JsonDerivedType(typeof(DomLocator), "dom")]
[JsonDerivedType(typeof(PointLocator), "point")]
public abstract record Locator;

public sealed record WindowLocator(string TitleRegex, string? ProcessName, string? ClassName) : Locator;

public sealed record UiaPathSegment(string ControlType, string? AutomationId, string? Name, int Index);

public sealed record UiaLocator(WindowLocator Window, IReadOnlyList<UiaPathSegment> Path) : Locator;

public sealed record DomLocator(WindowLocator Window, string Selector) : Locator;

/// <summary>
/// A screen point chosen on a screenshot (visual grounding), stored as fractions of the window rectangle so a replay
/// follows the window when it has moved or been resized.
/// </summary>
public sealed record PointLocator(WindowLocator Window, double Fx, double Fy) : Locator
{
    public static PointLocator FromScreen(WindowLocator window, Windows.Rect rect, int x, int y) =>
        new(window, rect.Width > 0 ? (x - rect.X) / (double)rect.Width : 0, rect.Height > 0 ? (y - rect.Y) / (double)rect.Height : 0);

    public (int X, int Y) ToScreen(Windows.Rect rect) =>
        ((int)Math.Round(rect.X + Fx * rect.Width), (int)Math.Round(rect.Y + Fy * rect.Height));
}

public sealed record ElementRef(string Id, RefKind Kind, Locator Locator, [property: JsonIgnore] object? Handle);
