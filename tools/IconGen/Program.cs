// Generates SwiftGet's icons: app .ico, logo, browser extension icons and installer artwork.
// Usage: dotnet run --project tools/IconGen -- <repo root>
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var assets = Path.Combine(root, "SwiftGet", "Assets");
var extIcons = Path.Combine(root, "extension", "icons");
var installer = Path.Combine(root, "installer");
Directory.CreateDirectory(assets);
Directory.CreateDirectory(extIcons);
Directory.CreateDirectory(installer);

// app icon
var icoSizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
WriteIco(Path.Combine(assets, "SwiftGet.ico"), icoSizes.Select(Render).ToList());
using (var logo = Render(256)) logo.Save(Path.Combine(assets, "logo.png"), ImageFormat.Png);

// browser extension
foreach (var s in new[] { 16, 32, 48, 128 })
    using (var b = Render(s)) b.Save(Path.Combine(extIcons, $"icon{s}.png"), ImageFormat.Png);

// installer artwork (24-bit BMP)
SaveBmp(WizardSmall(55), Path.Combine(installer, "wizard-small.bmp"));
SaveBmp(WizardSmall(110), Path.Combine(installer, "wizard-small-200.bmp"));
SaveBmp(WizardLarge(164, 314), Path.Combine(installer, "wizard-large.bmp"));
SaveBmp(WizardLarge(328, 628), Path.Combine(installer, "wizard-large-200.bmp"));
Console.WriteLine("Icons written.");

// ---------------------------------------------------------------------------------------------

static Bitmap Render(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    DrawMark(g, 0, 0, size);
    return bmp;
}

static void DrawMark(Graphics g, float x, float y, float s)
{
    float pad = s <= 20 ? 0 : s * 0.04f;
    var rect = new RectangleF(x + pad, y + pad, s - 2 * pad, s - 2 * pad);
    using (var path = RoundRect(rect, rect.Width * 0.26f))
    using (var fill = new LinearGradientBrush(new PointF(rect.Left, rect.Top), new PointF(rect.Right, rect.Bottom),
               Color.FromArgb(0x4F, 0x9B, 0xFF), Color.FromArgb(0x4F, 0x3C, 0xE8)))
    {
        g.FillPath(fill, path);
        // soft highlight on the upper half
        float hlHeight = rect.Height * 0.6f;
        using var hl = new LinearGradientBrush(new PointF(0, rect.Top - 1), new PointF(0, rect.Top + hlHeight + 1),
            Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
        var oldClip = g.Clip;
        g.SetClip(path);
        g.FillRectangle(hl, rect.Left, rect.Top, rect.Width, hlHeight);
        g.Clip = oldClip;
    }

    float w = Math.Max(1.6f, s * 0.115f);
    using var pen = new Pen(Color.White, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
    float cx = x + s / 2;
    g.DrawLine(pen, cx, y + s * 0.22f, cx, y + s * 0.60f);
    g.DrawLines(pen, new[]
    {
        new PointF(x + s * 0.31f, y + s * 0.45f),
        new PointF(cx, y + s * 0.64f),
        new PointF(x + s * 0.69f, y + s * 0.45f),
    });
    using var basePen = new Pen(Color.FromArgb(225, 255, 255, 255), Math.Max(1.4f, s * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    g.DrawLine(basePen, x + s * 0.29f, y + s * 0.79f, x + s * 0.71f, y + s * 0.79f);
}

static GraphicsPath RoundRect(RectangleF r, float radius)
{
    float d = radius * 2;
    var p = new GraphicsPath();
    p.AddArc(r.Left, r.Top, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

static Bitmap WizardSmall(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.White);
    DrawMark(g, size * 0.06f, size * 0.06f, size * 0.88f);
    return bmp;
}

static Bitmap WizardLarge(int w, int h)
{
    var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    using (var bg = new LinearGradientBrush(new Point(0, 0), new Point(w, h), Color.FromArgb(0x1E, 0x3A, 0x8A), Color.FromArgb(0x4C, 0x1D, 0x95)))
        g.FillRectangle(bg, 0, 0, w, h);
    // decorative circles
    using (var c = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
    {
        g.FillEllipse(c, -w * 0.4f, h * 0.62f, w * 1.1f, w * 1.1f);
        g.FillEllipse(c, w * 0.45f, -w * 0.35f, w * 0.9f, w * 0.9f);
    }
    float mark = w * 0.46f;
    DrawMark(g, (w - mark) / 2, h * 0.24f, mark);
    using var font = new Font("Segoe UI Semibold", w * 0.12f, FontStyle.Regular, GraphicsUnit.Pixel);
    using var small = new Font("Segoe UI", w * 0.068f, FontStyle.Regular, GraphicsUnit.Pixel);
    var fmt = new StringFormat { Alignment = StringAlignment.Center };
    g.DrawString("SwiftGet", font, Brushes.White, new RectangleF(0, h * 0.24f + mark + w * 0.08f, w, w * 0.2f), fmt);
    using var dim = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
    g.DrawString("Download Manager", small, dim, new RectangleF(0, h * 0.24f + mark + w * 0.24f, w, w * 0.15f), fmt);
    return bmp;
}

static void SaveBmp(Bitmap bmp, string path)
{
    using (bmp) bmp.Save(path, ImageFormat.Bmp);
}

/// <summary>Writes an .ico: classic DIB frames up to 64px, PNG frames above.</summary>
static void WriteIco(string path, List<Bitmap> frames)
{
    var blobs = frames.Select(f => f.Width >= 128 ? Png(f) : Dib(f)).ToList();
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
    int offset = 6 + 16 * frames.Count;
    for (int i = 0; i < frames.Count; i++)
    {
        int s = frames[i].Width;
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32);
        w.Write(blobs[i].Length); w.Write(offset);
        offset += blobs[i].Length;
    }
    foreach (var b in blobs) w.Write(b);
    foreach (var f in frames) f.Dispose();
}

static byte[] Png(Bitmap b)
{
    using var ms = new MemoryStream();
    b.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static byte[] Dib(Bitmap b)
{
    int s = b.Width;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
    w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = s - 1; y >= 0; y--)
        for (int x = 0; x < s; x++)
        {
            var c = b.GetPixel(x, y);
            w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
        }
    int maskRow = ((s + 31) / 32) * 4;
    for (int y = s - 1; y >= 0; y--)
    {
        var row = new byte[maskRow];
        for (int x = 0; x < s; x++)
            if (b.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
        w.Write(row);
    }
    return ms.ToArray();
}
