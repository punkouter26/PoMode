#!/usr/bin/env dotnet
// Layout audit and before/after capture for every route, at a phone and a desktop viewport.
//
//   dotnet run scripts/layout-audit.cs -- capture before [baseUrl]   (app must be running; default http://localhost:5000)
//   dotnet run scripts/layout-audit.cs -- capture after  [baseUrl]
//   dotnet run scripts/layout-audit.cs -- compare before after <notes.md> <name>
//
// capture writes SCREENSHOTS/audit/<label>/ (one PNG per route and viewport, plus results.json) and
// checks what docs/ui-rules.md promises: no sideways scroll, no page taller than the viewport except
// the two documents allowed to be, no text under 12px, no pointer target under 24px, no layout shift
// after first paint. compare writes one self-contained SCREENSHOTS/<name>.html with both states side
// by side, each annotated with its findings, and the notes file as the summary of what changed.
#:package Microsoft.Playwright@1.62.0
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false

using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

var repo = FindRepoRoot();
var shots = Path.Combine(repo, "SCREENSHOTS");
switch (args)
{
    case ["capture", var label, ..]:
        await CaptureAsync(Path.Combine(shots, "audit", label), args.Length > 2 ? args[2] : "http://localhost:5000");
        break;
    case ["compare", var before, var after, var notes, var name]:
        Compare(Path.Combine(shots, "audit", before), Path.Combine(shots, "audit", after), notes, Path.Combine(shots, $"{name}.html"));
        break;
    default:
        Console.Error.WriteLine("usage: capture <label> [baseUrl] | compare <before> <after> <notes.md> <name>");
        return 1;
}
return 0;

static async Task CaptureAsync(string outDir, string baseUrl)
{
    Directory.CreateDirectory(outDir);
    // A no-op once this Playwright version's Chromium is present; a fresh machine needs no manual step.
    if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
    {
        throw new InvalidOperationException("Playwright could not install Chromium.");
    }
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync();
    var viewports = new (string Name, int Width, int Height, bool Touch)[] { ("phone", 390, 844, true), ("desktop", 1440, 900, false) };
    var results = new List<PageResult>();

    // One guest per run: the first visit mints the session, and the library read seeds the demo.
    var state = Path.Combine(outDir, "session.json");
    {
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(baseUrl);
        await page.WaitForSelectorAsync("h1", new() { State = WaitForSelectorState.Attached, Timeout = 60000 });
        await page.WaitForTimeoutAsync(1500);
        await context.StorageStateAsync(new() { Path = state });
    }

    var routes = new List<(string Name, string Path, bool MayScroll)>
    {
        ("library", "/", false), ("mode-lab", "/modes", true), ("practice", "/practice", false), ("diagnostics", "/diagnostics", true),
    };
    {
        await using var context = await browser.NewContextAsync(new() { StorageStatePath = state });
        var page = await context.NewPageAsync();
        await page.GotoAsync(baseUrl);
        var jobId = await page.EvaluateAsync<string?>(@"async () => {
            const r = await fetch('api/library'); if (!r.ok) return null;
            const rows = await r.json(); const done = rows.find(x => x.stage === 'Complete' || x.stage === 5);
            return done ? done.jobId : null; }");
        if (jobId is not null)
        {
            routes.Add(("analysis", $"/?job={jobId}", true));
        }
    }

    foreach (var viewport in viewports)
    {
        foreach (var route in routes)
        {
            await using var context = await browser.NewContextAsync(new()
            {
                StorageStatePath = state,
                ViewportSize = new() { Width = viewport.Width, Height = viewport.Height },
                HasTouch = viewport.Touch,
                IsMobile = viewport.Touch,
                DeviceScaleFactor = viewport.Touch ? 2 : 1,
                ColorScheme = ColorScheme.Light,
            });
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            page.Console += (_, message) => { if (message.Type == "error") errors.Add(message.Text); };
            await page.AddInitScriptAsync(@"window.__cls = 0; new PerformanceObserver(list => {
                for (const e of list.getEntries()) if (!e.hadRecentInput) window.__cls += e.value; })
                .observe({ type: 'layout-shift', buffered: true });");
            await page.GotoAsync(baseUrl + route.Path);
            await page.WaitForSelectorAsync("h1", new() { State = WaitForSelectorState.Attached, Timeout = 60000 });
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); } catch (TimeoutException) { }
            await page.WaitForTimeoutAsync(2500);

            var findings = await page.EvaluateAsync<string[]>(@"(mayScroll) => {
                const out = [];
                const doc = document.documentElement;
                if (doc.scrollWidth > innerWidth + 1) out.push(`sideways scroll: page is ${doc.scrollWidth}px wide in a ${innerWidth}px viewport`);
                if (!mayScroll && doc.scrollHeight > innerHeight + 1) out.push(`vertical scroll: page is ${doc.scrollHeight}px tall in a ${innerHeight}px viewport`);
                const visible = (el) => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                    return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && !el.closest('.pm-sr-only,[hidden]'); };
                const label = (el) => (el.getAttribute('aria-label') || el.textContent || el.className || el.tagName).trim().replace(/\s+/g, ' ').slice(0, 40);
                const small = new Set();
                for (const el of document.querySelectorAll('body *')) {
                    if (!visible(el)) continue;
                    const own = [...el.childNodes].some(n => n.nodeType === 3 && n.textContent.trim());
                    if (own && parseFloat(getComputedStyle(el).fontSize) < 11.99) small.add(`${label(el)} (${getComputedStyle(el).fontSize})`);
                }
                for (const s of [...small].slice(0, 8)) out.push(`text under 12px: ${s}`);
                let tiny = 0; const examples = [];
                for (const el of document.querySelectorAll('button, a[href], [role=button], [role=menuitem], input:not([type=hidden]), select')) {
                    if (!visible(el)) continue;
                    if (el.getBoundingClientRect().height < 23.5) { tiny++; if (examples.length < 4) examples.push(label(el)); }
                }
                if (tiny) out.push(`${tiny} target(s) under 24px tall, e.g. ${examples.join(' | ')}`);
                let clipped = 0;
                for (const el of document.querySelectorAll('body *')) {
                    if (!visible(el)) continue;
                    const s = getComputedStyle(el);
                    if ((s.overflowX === 'hidden' || s.textOverflow === 'ellipsis') && el.scrollWidth > el.clientWidth + 1 && el.clientWidth > 0) clipped++;
                }
                if (clipped) out.push(`${clipped} element(s) clip their own content`);
                if (window.__cls > 0.1) out.push(`layout shift after load: CLS ${window.__cls.toFixed(3)}`);
                return out; }", route.MayScroll);
            findings = [.. findings, .. errors.Distinct().Take(3).Select(e => $"console error: {e[..Math.Min(e.Length, 160)]}")];

            var file = $"{route.Name}-{viewport.Name}.png";
            await page.ScreenshotAsync(new() { Path = Path.Combine(outDir, file), FullPage = true });
            results.Add(new PageResult(route.Name, route.Path, viewport.Name, file, findings));
            Console.WriteLine($"{route.Name,-12} {viewport.Name,-8} {findings.Length} finding(s)");
            foreach (var finding in findings)
            {
                Console.WriteLine($"    - {finding}");
            }
        }
    }
    File.Delete(state);
    File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
}

static void Compare(string beforeDir, string afterDir, string notesPath, string outFile)
{
    var before = Load(beforeDir);
    var after = Load(afterDir);
    var html = new StringBuilder();
    html.Append("""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>PoMode before and after</title><style>
        :root{color-scheme:light dark;--bg:#fafafa;--fg:#18181b;--muted:#52525b;--card:#fff;--edge:#e4e4e7;--bad:#b91c1c;--good:#15803d}
        @media (prefers-color-scheme:dark){:root{--bg:#09090b;--fg:#f4f4f5;--muted:#a1a1aa;--card:#18181b;--edge:#27272a;--bad:#fca5a5;--good:#86efac}}
        body{margin:0;padding:16px;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,sans-serif}
        h1{margin:0 0 4px}h2{margin:32px 0 8px;font-size:18px}.notes{background:var(--card);border:1px solid var(--edge);border-radius:8px;padding:12px 16px;white-space:pre-wrap}
        .pair{display:grid;grid-template-columns:1fr 1fr;gap:16px}.pair>div{background:var(--card);border:1px solid var(--edge);border-radius:8px;padding:12px;min-width:0}
        .pair img{width:100%;height:auto;border:1px solid var(--edge);border-radius:4px}.tag{font-weight:600;color:var(--muted)}
        ul{margin:8px 0 0;padding-left:20px;font-size:13px}li.bad{color:var(--bad)}li.good{color:var(--good)}
        @media (max-width:640px){.pair{grid-template-columns:1fr}}</style></head><body>
        <h1>PoMode before and after</h1>
        """);
    html.Append($"<p class=\"tag\">Generated {DateTime.Now:yyyy-MM-dd HH:mm} by scripts/layout-audit.cs</p>");
    html.Append($"<div class=\"notes\">{Escape(File.Exists(notesPath) ? File.ReadAllText(notesPath) : "")}</div>");
    foreach (var shot in after)
    {
        var old = before.FirstOrDefault(b => b.Route == shot.Route && b.Viewport == shot.Viewport);
        html.Append($"<h2>{Escape(shot.Route)} · {shot.Viewport} <span class=\"tag\">{Escape(shot.Path)}</span></h2><div class=\"pair\">");
        html.Append(Panel("Before", beforeDir, old, null));
        html.Append(Panel("After", afterDir, shot, old));
        html.Append("</div>");
    }
    html.Append("</body></html>");
    File.WriteAllText(outFile, html.ToString());
    Console.WriteLine(outFile);

    static List<PageResult> Load(string dir) =>
        JsonSerializer.Deserialize<List<PageResult>>(File.ReadAllText(Path.Combine(dir, "results.json"))) ?? [];

    static string Panel(string title, string dir, PageResult? shot, PageResult? previous)
    {
        if (shot is null)
        {
            return $"<div><p class=\"tag\">{title}</p><p>Not captured (new route).</p></div>";
        }
        var image = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(dir, shot.File)));
        var items = new StringBuilder();
        foreach (var finding in shot.Findings)
        {
            items.Append($"<li class=\"bad\">{Escape(finding)}</li>");
        }
        if (previous is not null)
        {
            foreach (var fixedOne in previous.Findings.Where(f => !shot.Findings.Contains(f)))
            {
                items.Append($"<li class=\"good\">resolved: {Escape(fixedOne)}</li>");
            }
        }
        var list = items.Length > 0 ? $"<ul>{items}</ul>" : "<ul><li class=\"good\">no findings</li></ul>";
        return $"<div><p class=\"tag\">{title}</p><img alt=\"{title}: {Escape(shot.Route)} at {shot.Viewport}\" src=\"data:image/png;base64,{image}\">{list}</div>";
    }
}

static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoMode.slnx")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the PoMode repository.");
}

record PageResult(string Route, string Path, string Viewport, string File, string[] Findings);
