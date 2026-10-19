using System.Text.Json.Serialization;

namespace WinUse.Core.Refs;

public enum RefKind { Window, Uia, Dom }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WindowLocator), "window")]
[JsonDerivedType(typeof(UiaLocator), "uia")]
[JsonDerivedType(typeof(DomLocator), "dom")]
public abstract record Locator;

public sealed record WindowLocator(string TitleRegex, string? ProcessName, string? ClassName) : Locator;

public sealed record UiaPathSegment(string ControlType, string? AutomationId, string? Name, int Index);

public sealed record UiaLocator(WindowLocator Window, IReadOnlyList<UiaPathSegment> Path) : Locator;

public sealed record DomLocator(WindowLocator Window, string Selector) : Locator;

public sealed record ElementRef(string Id, RefKind Kind, Locator Locator, [property: JsonIgnore] object? Handle);
