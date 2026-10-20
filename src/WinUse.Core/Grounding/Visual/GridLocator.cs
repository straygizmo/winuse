using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WinUse.Core.Jev;
using WinUse.Core.Screen;

namespace WinUse.Core.Grounding.Visual;

/// <summary>
/// A PNG with its screen geometry: image pixel (px, py) is screen pixel <c>(OriginX + px / Scale, OriginY + py / Scale)</c>.
/// Every crop / resize produces a new frame whose origin and scale keep that mapping exact.
/// </summary>
public sealed record VisualFrame(byte[] Png, int Width, int Height, int OriginX, int OriginY, double Scale)
{
    public (int X, int Y) ToScreen(double px, double py) =>
        ((int)Math.Round(OriginX + px / Scale), (int)Math.Round(OriginY + py / Scale));

    public static VisualFrame FromCapture(Capture c) => new(c.Png, c.Width, c.Height, c.OriginX, c.OriginY, c.Scale);
}

public readonly record struct GridSpec(int Cols, int Rows);

/// <summary>A screen point chosen on a screenshot, with the model's probability / confidence for the final choice.</summary>
public readonly record struct VisualPoint(int X, int Y, double Probability, double Confidence);

/// <summary>
/// Locates a target on a screenshot with staged grid-cell <c>choice</c> questions (works with any provider), and maps
/// a Jev <c>box</c> answer to a screen point. Pure image / geometry helpers; the questions are asked by the caller.
/// </summary>
public static class GridLocator
{
    /// <summary>Longest side sent to the model; Clef resizes to about 1 MP anyway and Jev caps sides at 1568 px.</summary>
    public const int MaxSide = 1280;
    /// <summary>Jev rejects images with a side under 280 px.</summary>
    public const int MinSide = 280;
    /// <summary>Longest side of a refinement crop.</summary>
    public const int StageSide = 640;
    /// <summary>Stop refining once a cell is this small on screen.</summary>
    public const int TargetCellPx = 24;
    public const int MaxStages = 3;
    /// <summary>Margin added around the chosen cell before cropping, as a fraction of the cell size.</summary>
    public const double Margin = 0.25;

    public static readonly GridSpec FirstGrid = new(8, 6);
    public static readonly GridSpec RefineGrid = new(6, 4);
    public const string None = "none";

    // ---- geometry -------------------------------------------------------------------------------

    public static string CellId(int col, int row) => $"{(char)('A' + col)}{row + 1}";

    public static bool TryParseCell(string id, GridSpec g, out int col, out int row)
    {
        col = row = -1;
        if (id.Length < 2 || id[0] < 'A' || id[0] >= 'A' + g.Cols) return false;
        if (!int.TryParse(id.AsSpan(1), out var r) || r < 1 || r > g.Rows) return false;
        col = id[0] - 'A';
        row = r - 1;
        return true;
    }

    /// <summary>Choice criteria: every cell with a plain-words position, plus <see cref="None"/>.</summary>
    public static IReadOnlyDictionary<string, string?> Criteria(GridSpec g)
    {
        var c = new Dictionary<string, string?>();
        for (var row = 0; row < g.Rows; row++)
            for (var col = 0; col < g.Cols; col++)
            {
                var h = col == 0 ? " (leftmost)" : col == g.Cols - 1 ? " (rightmost)" : "";
                var v = row == 0 ? " (top)" : row == g.Rows - 1 ? " (bottom)" : "";
                c[CellId(col, row)] = $"column {(char)('A' + col)} of {g.Cols}{h}, row {row + 1} of {g.Rows}{v}";
            }
        c[None] = null;
        return c;
    }

    public static Rectangle CellRect(GridSpec g, int width, int height, int col, int row)
    {
        var x1 = Edge(col, g.Cols, width);
        var x2 = Edge(col + 1, g.Cols, width);
        var y1 = Edge(row, g.Rows, height);
        var y2 = Edge(row + 1, g.Rows, height);
        return new Rectangle(x1, y1, x2 - x1, y2 - y1);
    }

    static int Edge(int i, int n, int size) => (int)Math.Round(i * size / (double)n);

    /// <summary>The smaller side of a cell, in screen pixels.</summary>
    public static double CellScreenSize(VisualFrame f, GridSpec g) =>
        Math.Min(f.Width / (double)g.Cols, f.Height / (double)g.Rows) / f.Scale;

    public static (int X, int Y) BoxCentre(VisualFrame f, BoxAnswer box) =>
        f.ToScreen((box.Left + box.Right) / 2 * f.Width, (box.Top + box.Bottom) / 2 * f.Height);

    // ---- image operations -----------------------------------------------------------------------

    /// <summary>Scales the frame into the model's size window: longest side at most <see cref="MaxSide"/>, shortest at least <see cref="MinSide"/>.</summary>
    public static VisualFrame Fit(VisualFrame f)
    {
        var longest = Math.Max(f.Width, f.Height);
        var shortest = Math.Min(f.Width, f.Height);
        var factor = 1.0;
        if (longest > MaxSide) factor = MaxSide / (double)longest;
        if (shortest * factor < MinSide) factor = MinSide / (double)shortest;
        return Math.Abs(factor - 1) < 1e-9 ? f : Resize(f, factor);
    }

    public static VisualFrame Resize(VisualFrame f, double factor)
    {
        var w = Math.Max(1, (int)Math.Round(f.Width * factor));
        var h = Math.Max(1, (int)Math.Round(f.Height * factor));
        using var src = Load(f.Png);
        using var dst = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
        }
        return new VisualFrame(Encode(dst), w, h, f.OriginX, f.OriginY, f.Scale * factor);
    }

    /// <summary>Draws the grid lines and a cell label in the top-left corner of every cell. Geometry is unchanged.</summary>
    public static VisualFrame DrawGrid(VisualFrame f, GridSpec g)
    {
        using var bmp = Load(f.Png);
        using (var gr = Graphics.FromImage(bmp))
        {
            gr.SmoothingMode = SmoothingMode.None;
            using var line = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
            for (var c = 1; c < g.Cols; c++) gr.FillRectangle(line, Edge(c, g.Cols, bmp.Width), 0, 1, bmp.Height);
            for (var r = 1; r < g.Rows; r++) gr.FillRectangle(line, 0, Edge(r, g.Rows, bmp.Height), bmp.Width, 1);

            using var font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold, GraphicsUnit.Pixel);
            gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            for (var row = 0; row < g.Rows; row++)
                for (var col = 0; col < g.Cols; col++)
                {
                    var cell = CellRect(g, bmp.Width, bmp.Height, col, row);
                    var id = CellId(col, row);
                    var size = gr.MeasureString(id, font);
                    var box = new RectangleF(cell.X + 2, cell.Y + 2, size.Width + 2, size.Height + 1);
                    gr.FillRectangle(Brushes.White, box);
                    gr.DrawRectangle(Pens.Black, box.X, box.Y, box.Width, box.Height);
                    gr.DrawString(id, font, Brushes.Black, box.X + 1, box.Y);
                }
        }
        return new VisualFrame(Encode(bmp), f.Width, f.Height, f.OriginX, f.OriginY, f.Scale);
    }

    /// <summary>Cuts the chosen cell plus <see cref="Margin"/> out of the frame and enlarges it to <see cref="StageSide"/>.</summary>
    public static VisualFrame Crop(VisualFrame f, GridSpec g, int col, int row)
    {
        var cell = CellRect(g, f.Width, f.Height, col, row);
        var mx = (int)Math.Round(cell.Width * Margin);
        var my = (int)Math.Round(cell.Height * Margin);
        var x1 = Math.Max(0, cell.X - mx);
        var y1 = Math.Max(0, cell.Y - my);
        var x2 = Math.Min(f.Width, cell.Right + mx);
        var y2 = Math.Min(f.Height, cell.Bottom + my);
        var region = new Rectangle(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));

        var factor = StageSide / (double)Math.Max(region.Width, region.Height);
        var w = Math.Max(1, (int)Math.Round(region.Width * factor));
        var h = Math.Max(1, (int)Math.Round(region.Height * factor));
        using var src = Load(f.Png);
        using var dst = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using (var gr = Graphics.FromImage(dst))
        {
            gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gr.DrawImage(src, new Rectangle(0, 0, w, h), region, GraphicsUnit.Pixel);
        }
        var (ox, oy) = f.ToScreen(region.X, region.Y);
        return new VisualFrame(Encode(dst), w, h, ox, oy, f.Scale * factor);
    }

    // ---- staged search --------------------------------------------------------------------------

    /// <summary>
    /// Asks <paramref name="ask"/> which grid cell holds the target, zooms into that cell and asks again (at most
    /// <see cref="MaxStages"/> times, stopping early once a cell is <see cref="TargetCellPx"/> or smaller on screen).
    /// Returns null when the model answers <see cref="None"/>, an unknown cell, or below the thresholds.
    /// </summary>
    public static async Task<VisualPoint?> LocateAsync(VisualFrame full, Func<VisualFrame, GridSpec, Task<ChoiceAnswer>> ask,
        double minProbability, double minConfidence)
    {
        var frame = Fit(full);
        var grid = FirstGrid;
        for (var stage = 1; ; stage++)
        {
            var ans = await ask(DrawGrid(frame, grid), grid).ConfigureAwait(false);
            if (ans.Choice == None || !TryParseCell(ans.Choice, grid, out var col, out var row)) return null;
            var p = ans.Probabilities.TryGetValue(ans.Choice, out var pv) ? pv : 0;
            if (p < minProbability || ans.Confidence < minConfidence) return null;

            var cell = CellRect(grid, frame.Width, frame.Height, col, row);
            var onScreen = Math.Min(cell.Width, cell.Height) / frame.Scale;
            if (stage >= MaxStages || onScreen <= TargetCellPx)
            {
                var (x, y) = frame.ToScreen(cell.X + cell.Width / 2.0, cell.Y + cell.Height / 2.0);
                return new VisualPoint(x, y, p, ans.Confidence);
            }
            frame = Crop(frame, grid, col, row);
            grid = RefineGrid;
        }
    }

    // ---- codec ----------------------------------------------------------------------------------

    static Bitmap Load(byte[] png)
    {
        // GDI+ keeps reading from the stream for the bitmap's lifetime; cloning detaches it.
        using var ms = new MemoryStream(png);
        using var tmp = new Bitmap(ms);
        return new Bitmap(tmp);
    }

    static byte[] Encode(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
