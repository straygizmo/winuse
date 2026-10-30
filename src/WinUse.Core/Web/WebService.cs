using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;
using InputButton = WinUse.Core.Input.MouseButton;
using WinUse.Core.Input;
using WinUse.Core.Refs;
using WinUse.Core.Windows;

namespace WinUse.Core.Web;

/// <summary>
/// Drives WebView2/Chromium content over the DevTools protocol using Playwright.
/// Playwright is async and thread-safe, so calls are deliberately not routed through AutomationDispatcher.
/// </summary>
public sealed class WebService(RefRegistry refs) : IAsyncDisposable
{
    private const float ActionTimeoutMs = 5000;
    private static readonly Lazy<string> CollectorScript = new(LoadCollector);

    private readonly object _lock = new();
    private readonly Dictionary<IPage, int> _pageIds = [];
    private int _nextPageId = 1;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _current;

    public WindowLocator Window { get; set; } = new(".", null, null);

    public bool IsConnected => _browser?.IsConnected == true;

    internal IPage? CurrentPage
    {
        get { lock (_lock) return _current ?? FirstPage(); }
    }

    public async Task ConnectAsync(int cdpPort, TimeSpan timeout)
    {
        var url = $"http://127.0.0.1:{cdpPort}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var resp = await http.GetAsync(url + "/json/version");
                if (resp.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            if (sw.Elapsed >= timeout)
                throw new TimeoutException($"CDP endpoint on port {cdpPort} did not respond within {timeout}");
            await Task.Delay(250);
        }

        await DisposeAsync(); // drop any previous session (and its driver process)
        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.ConnectOverCDPAsync(url);
            foreach (var ctx in _browser.Contexts) Attach(ctx);
            lock (_lock) _current = FirstPage();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<PageInfo>> ListPagesAsync()
    {
        EnsureConnected();
        var result = new List<PageInfo>();
        foreach (var page in AllPages())
        {
            string title;
            try { title = await page.TitleAsync(); } catch (PlaywrightException) { title = ""; }
            result.Add(new PageInfo(IdOf(page), page.Url, title));
        }
        return result;
    }

    public async Task SelectPageAsync(string idOrUrlSubstring)
    {
        EnsureConnected();
        var pages = AllPages();
        var match = pages.FirstOrDefault(p => IdOf(p) == idOrUrlSubstring)
            ?? pages.FirstOrDefault(p => p.Url.Contains(idOrUrlSubstring, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            foreach (var p in pages)
            {
                string title;
                try { title = await p.TitleAsync(); } catch (PlaywrightException) { continue; }
                if (title.Contains(idOrUrlSubstring, StringComparison.OrdinalIgnoreCase)) { match = p; break; }
            }
        }
        if (match is null) throw new ArgumentException($"No page matches: {idOrUrlSubstring}");
        lock (_lock) _current = match;
    }

    public async Task<IReadOnlyList<DomNode>> GetDomAsync(bool interactiveOnly = true)
    {
        var page = RequirePage();
        refs.BeginSnapshot();
        var arr = await page.EvaluateAsync<JsonElement>(CollectorScript.Value, interactiveOnly);
        var nodes = new List<DomNode>();
        foreach (var e in arr.EnumerateArray())
        {
            var selector = e.GetProperty("selector").GetString()!;
            var css = e.GetProperty("cssPath").GetString()!;
            // A selector we hand out must find exactly one element in Playwright's engine; else use the CSS path.
            if (selector != css && await page.Locator(selector).CountAsync() != 1) selector = css;
            var r = refs.Register(RefKind.Dom, new DomLocator(Window, selector), selector);
            var rect = e.GetProperty("rect");
            nodes.Add(new DomNode(
                r.Id,
                e.GetProperty("role").GetString()!,
                Str(e, "name"),
                e.GetProperty("tag").GetString()!,
                Str(e, "value"),
                e.GetProperty("enabled").GetBoolean(),
                e.GetProperty("focused").GetBoolean(),
                e.GetProperty("checked").ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => (bool?)null,
                },
                new Rect(
                    (int)Math.Round(rect.GetProperty("x").GetDouble()),
                    (int)Math.Round(rect.GetProperty("y").GetDouble()),
                    (int)Math.Round(rect.GetProperty("w").GetDouble()),
                    (int)Math.Round(rect.GetProperty("h").GetDouble())),
                selector));
        }
        return nodes;
    }

    public async Task ClickAsync(string refId)
    {
        var (page, sel) = Target(refId);
        await page.Locator(sel).ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs });
    }

    /// <summary>Clicks with a specific button/count; the offset is relative to the element centre.</summary>
    public async Task ClickAsync(string refId, InputButton button, int clickCount, int offsetX = 0, int offsetY = 0)
    {
        var (page, sel) = Target(refId);
        var loc = page.Locator(sel);
        var opts = new LocatorClickOptions
        {
            Timeout = ActionTimeoutMs,
            ClickCount = clickCount,
            Button = button switch
            {
                InputButton.Right => Microsoft.Playwright.MouseButton.Right,
                InputButton.Middle => Microsoft.Playwright.MouseButton.Middle,
                _ => Microsoft.Playwright.MouseButton.Left,
            },
        };
        if (offsetX != 0 || offsetY != 0)
        {
            var box = await loc.BoundingBoxAsync(new LocatorBoundingBoxOptions { Timeout = ActionTimeoutMs })
                ?? throw new InvalidOperationException($"DOM ref {refId} is not visible");
            opts.Position = new Position { X = box.Width / 2 + offsetX, Y = box.Height / 2 + offsetY };
        }
        await loc.ClickAsync(opts);
    }

    public async Task HoverAsync(string refId)
    {
        var (page, sel) = Target(refId);
        await page.Locator(sel).HoverAsync(new LocatorHoverOptions { Timeout = ActionTimeoutMs });
    }

    public async Task DragToAsync(string fromRefId, string toRefId)
    {
        var (page, from) = Target(fromRefId);
        var (_, to) = Target(toRefId);
        await page.Locator(from).DragToAsync(page.Locator(to), new LocatorDragToOptions { Timeout = ActionTimeoutMs });
    }

    /// <summary>Moves the mouse over the element and scrolls the wheel by the given CSS pixel deltas.</summary>
    public async Task ScrollAsync(string refId, float deltaX, float deltaY)
    {
        var (page, sel) = Target(refId);
        var loc = page.Locator(sel);
        await loc.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = ActionTimeoutMs });
        await loc.HoverAsync(new LocatorHoverOptions { Timeout = ActionTimeoutMs });
        await page.Mouse.WheelAsync(deltaX, deltaY);
    }

    public async Task SetCheckedAsync(string refId, bool on)
    {
        var (page, sel) = Target(refId);
        await page.Locator(sel).SetCheckedAsync(on, new LocatorSetCheckedOptions { Timeout = ActionTimeoutMs });
    }

    public async Task FocusAsync(string refId)
    {
        var (page, sel) = Target(refId);
        await page.Locator(sel).FocusAsync(new LocatorFocusOptions { Timeout = ActionTimeoutMs });
    }

    public async Task<bool> IsEnabledAsync(string refId)
    {
        var (page, sel) = Target(refId);
        return await page.Locator(sel).IsEnabledAsync(new LocatorIsEnabledOptions { Timeout = ActionTimeoutMs });
    }

    /// <summary>PNG screenshot of a single DOM element (CSS pixels of the page).</summary>
    public async Task<byte[]> ScreenshotAsync(string refId)
    {
        var (page, sel) = Target(refId);
        return await page.Locator(sel).ScreenshotAsync(new LocatorScreenshotOptions { Timeout = ActionTimeoutMs });
    }

    public async Task FillAsync(string refId, string text)
    {
        var (page, sel) = Target(refId);
        var loc = page.Locator(sel);
        try
        {
            await loc.FillAsync(text, new LocatorFillOptions { Timeout = ActionTimeoutMs });
        }
        catch (PlaywrightException)
        {
            await loc.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs });
            await page.Keyboard.PressAsync("Control+A");
            await page.Keyboard.TypeAsync(text);
        }
    }

    /// <summary>Selects the option by label and reads the select's current option label back; a different label is a mismatch error.</summary>
    public async Task<SelectItemResult> SelectOptionAsync(string refId, string label)
    {
        var (page, sel) = Target(refId);
        var locator = page.Locator(sel);
        await locator.SelectOptionAsync(new SelectOptionValue { Label = label },
            new LocatorSelectOptionOptions { Timeout = ActionTimeoutMs });
        string? selected;
        try
        {
            selected = await locator.EvaluateAsync<string?>(
                "el => el.selectedIndex >= 0 && el.options ? (el.options[el.selectedIndex].label || el.options[el.selectedIndex].text) : null");
        }
        catch (Exception ex)
        {
            return SelectItemResult.Unverified(SelectPath.Dom, label, $"reading the selected option failed: {ex.Message}");
        }
        if (selected is null) return SelectItemResult.Unverified(SelectPath.Dom, label, "the element reports no selected option");
        if (selected == label || string.Equals(selected.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase))
            return SelectItemResult.Did(SelectPath.Dom, label, selected);
        throw SelectItemResult.Mismatch(refId, "select", label, selected);
    }

    public async Task<string> GetTextAsync(string refId)
    {
        var (page, sel) = Target(refId);
        return await TextOf(page.Locator(sel));
    }

    public async Task<bool> ExistsAsync(string selector)
    {
        var page = RequirePage();
        return await page.Locator(selector).CountAsync() > 0;
    }

    public async Task<string> GetTextBySelectorAsync(string selector)
    {
        var page = RequirePage();
        return await TextOf(page.Locator(selector));
    }

    public async Task<string?> ResolveAsync(DomLocator loc)
    {
        var page = RequirePage();
        return await page.Locator(loc.Selector).CountAsync() == 1 ? loc.Selector : null;
    }

    public async ValueTask DisposeAsync()
    {
        var browser = _browser;
        var pw = _playwright;
        _browser = null;
        _playwright = null;
        lock (_lock) { _current = null; _pageIds.Clear(); }
        if (browser is not null)
        {
            // Disconnects from a CDP-attached browser without closing the user's app.
            try { await browser.CloseAsync(); } catch (PlaywrightException) { }
        }
        pw?.Dispose();
    }

    private static async Task<string> TextOf(ILocator locator)
    {
        var tag = await locator.EvaluateAsync<string>("e => e.tagName", new LocatorEvaluateOptions { Timeout = ActionTimeoutMs });
        return tag.ToUpperInvariant() is "INPUT" or "TEXTAREA" or "SELECT"
            ? await locator.InputValueAsync(new LocatorInputValueOptions { Timeout = ActionTimeoutMs })
            : await locator.InnerTextAsync(new LocatorInnerTextOptions { Timeout = ActionTimeoutMs });
    }

    private (IPage Page, string Selector) Target(string refId)
    {
        var page = RequirePage();
        if (!refs.TryGet(refId, out var r) || r.Kind != RefKind.Dom || r.Locator is not DomLocator dl)
            throw new ArgumentException($"Unknown DOM ref: {refId}");
        return (page, dl.Selector);
    }

    private IPage RequirePage()
    {
        EnsureConnected();
        return CurrentPage ?? throw new InvalidOperationException("No page available");
    }

    private void EnsureConnected()
    {
        if (!IsConnected) throw new InvalidOperationException("Web service is not connected");
    }

    private void Attach(IBrowserContext ctx)
    {
        foreach (var p in ctx.Pages) Track(p);
        ctx.Page += (_, p) =>
        {
            Track(p);
            lock (_lock) _current = p;
        };
    }

    private void Track(IPage p)
    {
        Track2(p);
        p.Load += (_, _) => { lock (_lock) _current = p; };
        p.Close += (_, _) => { lock (_lock) { if (ReferenceEquals(_current, p)) _current = null; } };
    }

    private List<IPage> AllPages()
    {
        var list = new List<IPage>();
        if (_browser is null) return list;
        foreach (var ctx in _browser.Contexts)
            foreach (var p in ctx.Pages)
            {
                Track2(p);
                list.Add(p);
            }
        return list;
    }

    // Pages created before we attached an event handler still need a stable id.
    private void Track2(IPage p)
    {
        lock (_lock)
        {
            if (!_pageIds.ContainsKey(p)) _pageIds[p] = _nextPageId++;
        }
    }

    private string IdOf(IPage p)
    {
        lock (_lock) return "p" + _pageIds[p];
    }

    private IPage? FirstPage() => AllPages().FirstOrDefault();

    private static string? Str(JsonElement e, string prop) =>
        e.GetProperty(prop).ValueKind == JsonValueKind.Null ? null : e.GetProperty(prop).GetString();

    private static string LoadCollector()
    {
        var asm = typeof(WebService).Assembly;
        using var s = asm.GetManifestResourceStream("WinUse.Core.Web.DomCollector.js")
            ?? throw new InvalidOperationException("Embedded resource DomCollector.js not found");
        using var rd = new StreamReader(s);
        return rd.ReadToEnd();
    }
}
