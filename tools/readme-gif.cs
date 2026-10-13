#:package Magick.NET-Q8-AnyCPU@14.17.2
#:property PublishAot=false

// Builds media/tour.gif, the captioned tour at the top of the README and the wiki's Home page, from
// the screenshots in media/. Re-run it after retaking any of them:
//
//   dotnet run tools/readme-gif.cs
//
// Run from the repository root, on Windows: the captions use Segoe UI from C:\Windows\Fonts.

using ImageMagick;
using ImageMagick.Drawing;

(string File, string Title, string Line)[] slides =
[
    ("credentialsScreen.png", "Sign in once",
        "OAuth2 for GitHub, Google or any provider. Tokens refresh in the background."),
    ("mcpFunnelScreen.png", "MCP Funnel: one endpoint per agent",
        "Pool several MCP servers and expose only the tools that agent should see."),
    ("apiMcpBridgeScreen.png", "API to MCP: any REST API becomes MCP tools",
        "Describe the API in a JSON manifest, and the agent gets tools for it."),
    ("openAPIPicker.png", "Or import an OpenAPI spec",
        "Tick the operations you want, and each becomes a tool."),
];

const uint Width = 960, CaptionHeight = 92, ShotHeight = 806, Height = CaptionHeight + ShotHeight;
const uint HoldCs = 300, FadeFrames = 4, FadeCs = 6;   // GIF delays are in hundredths of a second

var background = new MagickColor("#1f1f1f");
var band = new MagickColor("#161616");
var accent = new MagickColor("#f0a020");
const string Regular = @"C:\Windows\Fonts\segoeui.ttf", Bold = @"C:\Windows\Fonts\segoeuib.ttf";

var frames = slides.Select((s, i) => Render(s, i)).ToList();

using var gif = new MagickImageCollection();
for (var i = 0; i < frames.Count; i++)
{
    AddFrame(frames[i].Clone(), HoldCs);
    var next = frames[(i + 1) % frames.Count];
    for (var f = 1; f <= FadeFrames; f++)
    {
        var blend = frames[i].Clone();
        blend.Composite(next, CompositeOperator.Blend, $"{100 * f / (FadeFrames + 1)}");
        AddFrame(blend, FadeCs);
    }
}
gif[0].AnimationIterations = 0;
gif.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.No });
gif.Optimize();
gif.Write("media/tour.gif");
Console.WriteLine($"media/tour.gif: {new FileInfo("media/tour.gif").Length / 1024} KB, {gif.Count} frames");

void AddFrame(IMagickImage<byte> frame, uint delayCs)
{
    frame.AnimationDelay = delayCs;
    gif.Add(frame);
}

MagickImage Render((string File, string Title, string Line) slide, int index)
{
    var canvas = new MagickImage(background, Width, Height);
    using var shot = new MagickImage(Path.Combine("media", slide.File));
    if (shot.Width > Width || shot.Height > ShotHeight)
        shot.Resize(new MagickGeometry(Width, ShotHeight));   // fits inside, keeping the aspect ratio
    canvas.Composite(shot, (int)(Width - shot.Width) / 2, (int)(CaptionHeight + (ShotHeight - shot.Height) / 2),
        CompositeOperator.Over);

    new Drawables()
        .FillColor(band).Rectangle(0, 0, Width, CaptionHeight)
        .FillColor(accent).Rectangle(0, CaptionHeight - 3, Width * (index + 1.0) / slides.Length, CaptionHeight)
        .Font(Bold).FontPointSize(15).FillColor(accent).Text(Width - 64, 34, $"{index + 1} / {slides.Length}")
        .Font(Bold).FontPointSize(26).FillColor(MagickColors.White).Text(24, 42, slide.Title)
        .Font(Regular).FontPointSize(17).FillColor(new MagickColor("#b8b8b8")).Text(24, 74, slide.Line)
        .Draw(canvas);
    return canvas;
}
