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
File.Copy("site/index.html", Path.Combine(outDir, "index.html"), true);

// The sidebar's order is the reading order, so it also drives the previous/next links.
var sidebarMd = File.ReadAllText(Path.Combine(wikiDir, "_Sidebar.md"));
var order = Regex.Matches(sidebarMd, @"\]\(([^)#]+)\)").Select(m => m.Groups[1].Value).ToList();
var titles = Regex.Matches(sidebarMd, @"\[([^\]]+)\]\(([^)#]+)\)")
    .ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value);

var pages = Directory.GetFiles(wikiDir, "*.md")
    .Select(Path.GetFileNameWithoutExtension)
    .Where(p => !p!.StartsWith('_'))
    .ToList();

var missing = new List<string>();
foreach (var page in pages)
{
    var doc = Markdown.Parse(File.ReadAllText(Path.Combine(wikiDir, page + ".md")), pipeline);
    Rewrite(doc, page!, pages!, missing);
    var body = doc.ToHtml(pipeline);

    var sidebarDoc = Markdown.Parse(sidebarMd, pipeline);
    Rewrite(sidebarDoc, page!, pages!, missing);
    var sidebar = sidebarDoc.ToHtml(pipeline)
        .Replace($"href=\"{FileFor(page!)}\"", $"href=\"{FileFor(page!)}\" aria-current=\"page\"");

    var title = titles.GetValueOrDefault(page!, page!.Replace('-', ' '));
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
          <link rel="icon" href="../media/logo.png">
          <link rel="stylesheet" href="../style.css">
        </head>
        <body class="docs">
          <header class="topbar">
            <a class="brand" href="../"><img src="../media/logo.png" alt="" width="28" height="28">RavensPort</a>
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
                {{(prev is null ? "<span></span>" : $"<a href=\"{FileFor(prev)}\">← {Enc(titles.GetValueOrDefault(prev, prev))}</a>")}}
                {{(next is null ? "<span></span>" : $"<a class=\"next\" href=\"{FileFor(next)}\">{Enc(titles.GetValueOrDefault(next, next))} →</a>")}}
              </nav>
              <p class="edit"><a href="{{Repo}}/wiki/{{page}}">Edit this page on the wiki</a></p>
            </main>
          </div>
          <script>
            // The contents list is collapsible only on narrow screens, where it sits above the page.
            if (matchMedia("(max-width: 860px)").matches) document.querySelector(".sidebar").open = false;
          </script>
        </body>
        </html>
        """);
}

foreach (var m in missing.Distinct())
    Console.Error.WriteLine($"warning: {m}");
Console.WriteLine($"Built {pages.Count} pages into {outDir}");
return 0;

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
