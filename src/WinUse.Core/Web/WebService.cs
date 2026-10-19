using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;
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

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.ConnectOverCDPAsync(url);
        foreach (var ctx in _browser.Contexts) Attach(ctx);
        lock (_lock) _current = FirstPage();
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

    public async Task FillAsync(string refId, string text)
    {
        var (page, sel) = Target(refId);
        var loc = page.Locator(sel);
        var editable = await loc.EvaluateAsync<bool>("e => e.isContentEditable === true && !('value' in e)",
            new LocatorEvaluateOptions { Timeout = ActionTimeoutMs });
        if (editable)
        {
            await loc.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs });
            await page.Keyboard.TypeAsync(text);
        }
        else
        {
            await loc.FillAsync(text, new LocatorFillOptions { Timeout = ActionTimeoutMs });
        }
    }

    public async Task SelectOptionAsync(string refId, string label)
    {
        var (page, sel) = Target(refId);
        await page.Locator(sel).SelectOptionAsync(new SelectOptionValue { Label = label },
            new LocatorSelectOptionOptions { Timeout = ActionTimeoutMs });
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
