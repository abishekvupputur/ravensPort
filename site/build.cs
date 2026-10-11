#:package Markdig@1.4.0
// File-based apps default to native AOT, which puts the ILCompiler for this machine's runtime in
// packages.lock.json and then fails locked restore on any other OS. Nothing here needs AOT.
#:property PublishAot=false

// Builds the GitHub Pages site: site/index.html as the landing page, plus one page per wiki page
// under docs/. The wiki stays the single place documentation is written; this only renders it.
//
//   dotnet run site/build.cs -- <wiki checkout> <output dir>
//
// Run from the repository root. pages.yml runs it on every push to main and every wiki edit.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run site/build.cs -- <wiki dir> <output dir>");
    return 1;
}

var wikiDir = args[0];
var outDir = args[1];
const string Repo = "https://github.com/abishekvupputur/ravensPort";
// Wiki pages point their screenshots at raw.githubusercontent; the site serves its own copy.
const string RawMedia = "https://raw.githubusercontent.com/abishekvupputur/ravensPort/main/media/";

var pipeline = new MarkdownPipelineBuilder()
    .UseAdvancedExtensions() // pipe tables, auto identifiers, task lists, autolinks, ...
    .UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub)
    .Build();

Directory.CreateDirectory(Path.Combine(outDir, "docs"));
Directory.CreateDirectory(Path.Combine(outDir, "media"));
foreach (var f in Directory.GetFiles("media"))
    File.Copy(f, Path.Combine(outDir, "media", Path.GetFileName(f)), true);
File.Copy("site/style.css", Path.Combine(outDir, "style.css"), true);
File.Copy("site/site.js", Path.Combine(outDir, "site.js"), true);
File.Copy("site/index.html", Path.Combine(outDir, "index.html"), true);

// The sidebar's order is the reading order, so it also drives the previous/next links.
var sidebarMd = File.ReadAllText(Path.Combine(wikiDir, "_Sidebar.md"));
var order = Regex.Matches(sidebarMd, @"\]\(([^)#]+)\)").Select(m => m.Groups[1].Value).ToList();
var titles = Regex.Matches(sidebarMd, @"\[([^\]]+)\]\(([^)#]+)\)")
    .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value);

var pages = Directory.GetFiles(wikiDir, "*.md")
    .Select(p => Path.GetFileNameWithoutExtension(p)!)
    .Where(p => !p.StartsWith('_'))
    .OrderBy(p => p)
    .ToList();

// A page the sidebar forgot is still a page: it goes on the end of the list rather than being
// reachable only by someone who already knows its name.
var unlisted = pages.Where(p => !order.Contains(p)).ToList();
foreach (var p in unlisted)
{
    var heading = Regex.Match(File.ReadAllText(Path.Combine(wikiDir, p + ".md")), @"^#\s+(.+)$", RegexOptions.Multiline);
    titles[p] = heading.Success ? Regex.Replace(heading.Groups[1].Value, "<[^>]+>", "").Trim() : p.Replace('-', ' ');
    order.Add(p);
    sidebarMd += $"\n- [{titles[p]}]({p})";
}

var missing = new List<string>();
var search = new List<object>();
var headingPattern = new Regex(@"<h([23]) id=""([^""]+)"">(.*?)</h\1>", RegexOptions.Singleline);

foreach (var page in pages)
{
    var doc = Markdown.Parse(File.ReadAllText(Path.Combine(wikiDir, page + ".md")), pipeline);
    Rewrite(doc, page, pages, missing);
    var body = doc.ToHtml(pipeline);
    var title = titles.GetValueOrDefault(page, page.Replace('-', ' '));

    // Search entries, one per section, so a result lands on the paragraph rather than the top.
    var cuts = headingPattern.Matches(body).ToList();
    var summary = title;
    for (var s = -1; s < cuts.Count; s++)
    {
        var start = s < 0 ? 0 : cuts[s].Index + cuts[s].Length;
        var end = s + 1 < cuts.Count ? cuts[s + 1].Index : body.Length;
        var text = PlainText(body[start..end]);
        if (text.Length == 0 && s < 0) continue;
        if (summary == title && text.Length > 0) summary = text.Length > 160 ? text[..160] : text;

        search.Add(new
        {
            p = FileFor(page),
            t = title,
            h = s < 0 ? "" : PlainText(cuts[s].Groups[3].Value),
            a = s < 0 ? "" : cuts[s].Groups[2].Value,
            x = text.Length > 700 ? text[..700] : text,
        });
    }

    // The outline beside the page, from the same headings.
    var toc = string.Concat(cuts.Select(m =>
        $"<li class=\"l{m.Groups[1].Value}\"><a href=\"#{m.Groups[2].Value}\">{Enc(PlainText(m.Groups[3].Value))}</a></li>"));

    // A link on every heading, so any section can be shared.
    body = headingPattern.Replace(body, m =>
        $"<h{m.Groups[1].Value} id=\"{m.Groups[2].Value}\">{m.Groups[3].Value}"
        + $"<a class=\"anchor\" href=\"#{m.Groups[2].Value}\" aria-label=\"Link to this section\">#</a></h{m.Groups[1].Value}>");

    var sidebarDoc = Markdown.Parse(sidebarMd, pipeline);
    Rewrite(sidebarDoc, page, pages, missing);
    var sidebar = sidebarDoc.ToHtml(pipeline)
        .Replace($"href=\"{FileFor(page)}\"", $"href=\"{FileFor(page)}\" aria-current=\"page\"");

    var i = order.IndexOf(page);
    var prev = i > 0 ? order[i - 1] : null;
    var next = i >= 0 && i < order.Count - 1 ? order[i + 1] : null;

    File.WriteAllText(Path.Combine(outDir, "docs", FileFor(page)), $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{Enc(title)}} · RavensPort docs</title>
          <meta name="description" content="{{Enc(summary)}}">
          <link rel="icon" href="../media/logo.png">
          <link rel="stylesheet" href="../style.css">
          <script src="../site.js" data-index="search.json" defer></script>
        </head>
        <body class="docs">
          <header class="topbar">
            <a class="brand" href="../"><img src="../media/logo.png" alt="" width="28" height="28">RavensPort</a>
            <div class="search" role="search">
              <input type="search" id="q" placeholder="Search the docs" aria-label="Search the docs" autocomplete="off">
              <kbd>/</kbd>
              <ol id="results" hidden></ol>
            </div>
            <nav>
              <a href="index.html">Docs</a>
              <a href="{{Repo}}/releases">Releases</a>
              <a href="{{Repo}}">GitHub</a>
            </nav>
          </header>
          <div class="layout">
            <details class="sidebar" open>
              <summary>Contents</summary>
              {{sidebar}}
            </details>
            <main class="prose">
              {{body}}
              <nav class="pager">
                {{(prev is null ? "<span></span>" : $"<a href=\"{FileFor(prev)}\"><small>Previous</small>{Enc(titles.GetValueOrDefault(prev, prev))}</a>")}}
                {{(next is null ? "<span></span>" : $"<a class=\"next\" href=\"{FileFor(next)}\"><small>Next</small>{Enc(titles.GetValueOrDefault(next, next))}</a>")}}
              </nav>
              <p class="edit"><a href="{{Repo}}/wiki/{{page}}">Edit this page on the wiki</a></p>
            </main>
            {{(toc.Length == 0 ? "" : $"<nav class=\"toc\" aria-label=\"On this page\"><p>On this page</p><ul>{toc}</ul></nav>")}}
          </div>
        </body>
        </html>
        """);
}

File.WriteAllText(Path.Combine(outDir, "docs", "search.json"), System.Text.Json.JsonSerializer.Serialize(search));

foreach (var p in unlisted)
    Console.Error.WriteLine($"warning: {p} is not in _Sidebar.md; listed at the end");
foreach (var m in missing.Distinct())
    Console.Error.WriteLine($"warning: {m}");
Console.WriteLine($"Built {pages.Count} pages into {outDir}");
return 0;

// Text a reader would see: tags gone, entities decoded, whitespace collapsed.
static string PlainText(string html) =>
    Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

static string FileFor(string page) => page == "Home" ? "index.html" : page + ".html";

static string Enc(string s) => WebUtility.HtmlEncode(s);

// Wiki links are bare page names ("Routes", "Installation#from-a-release"); on the site each page
// is a file. Fragments that point at no heading are collected as warnings rather than failing the
// build, since a wiki edit should never leave the site stale.
static void Rewrite(MarkdownDocument doc, string page, List<string> pages, List<string> missing)
{
    foreach (var link in doc.Descendants<LinkInline>())
    {
        var url = link.Url ?? "";
        if (url.StartsWith(RawMedia))
        {
            link.Url = "../media/" + url[RawMedia.Length..];
            continue;
        }
        if (url.Length == 0 || url.StartsWith('#') || url.Contains(':') || url.StartsWith('/'))
            continue;

        var hash = url.IndexOf('#');
        var target = hash < 0 ? url : url[..hash];
        var fragment = hash < 0 ? "" : url[hash..];
        if (!pages.Contains(target))
        {
            missing.Add($"{page}: link to unknown wiki page '{target}'");
            continue;
        }
        link.Url = FileFor(target) + fragment;
    }
}
