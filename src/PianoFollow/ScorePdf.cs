using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PianoFollow.Core;

namespace PianoFollow;

/// <summary>将完整简谱排成 A4 横向 PDF，复用应用的简谱符号和节拍布局。</summary>
internal static class ScorePdf
{
    // WPF 使用每英寸 96 个设备无关像素，PDF 页大小以毫米设置。
    private const double Width = 297 * 96 / 25.4;
    private const double Height = 210 * 96 / 25.4;
    private const double Dpi = 240;
    private const double Margin = 38;
    private const double ScoreTop = 105;
    private const double ScoreWidth = Width - 2 * Margin;
    private const double ScoreHeight = Height - ScoreTop - 65;
    private static readonly Typeface Regular = new("Microsoft YaHei UI");
    private static readonly Typeface Bold = new(new FontFamily("Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush Ink = Brushes.Black;
    private static readonly Brush Muted = FrozenBrush("#4A4A4A");

    /// <summary>一行谱在纸面上的缩放比例和排版高度。缩放用于容纳极宽音域的复音谱行。</summary>
    private sealed record PrintedRow(ScoreLine Line, double OriginalHeight, double Scale)
    {
        public double Height => OriginalHeight * Scale;
    }

    /// <summary>在独立 STA 线程绘制 PDF，成功后才替换目标文件。</summary>
    internal static Task SaveAsync(string path, NumberScore score, string title)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Save(path, score, title);
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }) { Name = "PianoFollow PDF export" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Save(string path, NumberScore score, string title)
    {
        var pages = Paginate(score);
        using var document = new PdfDocument();
        document.Info.Title = $"{title} - 简谱";
        document.Info.Creator = "Piano Follow";
        for (int i = 0; i < pages.Count; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromMillimeter(297);
            page.Height = XUnit.FromMillimeter(210);

            using var png = DrawPage(pages[i], score, title, i + 1, pages.Count);
            using var image = XImage.FromStream(png);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        }

        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        string temporary = Path.Combine(directory, $".piano-score-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                document.Save(stream, false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary>按完整谱行分页，各页覆盖不重叠且连续的小节范围。</summary>
    private static List<List<PrintedRow>> Paginate(NumberScore score)
    {
        int columns = ScoreLineView.ColumnsForWidth(ScoreWidth, score.MeasureTicks);
        var pages = new List<List<PrintedRow>>();
        var current = new List<PrintedRow>();
        double used = 0;
        foreach (var measures in score.Measures.Chunk(columns))
        {
            var line = new ScoreLine(score, measures, columns);
            double height = ScoreLineView.LineHeight(line);
            var row = new PrintedRow(line, height, Math.Min(1, ScoreHeight / height));
            if (current.Count > 0 && used + row.Height > ScoreHeight + 0.01)
            {
                pages.Add(current);
                current = [];
                used = 0;
            }
            current.Add(row);
            used += row.Height;
        }
        if (current.Count > 0)
            pages.Add(current);
        return pages;
    }

    /// <summary>用 240 DPI 绘制一页，保留中文、八度点和连音等谱面细节。</summary>
    private static MemoryStream DrawPage(List<PrintedRow> rows, NumberScore score, string title, int page, int total)
    {
        var visual = new DrawingVisual();
        double scale = Dpi / 96;
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            DrawText(context, "钢琴跟谱  /  数字简谱", Margin, 24, 12, Muted, Regular);
            DrawText(context, title, Margin, 43, 27, Ink, Bold, ScoreWidth);
            int first = rows[0].Line.Measures[0].Number;
            int last = rows[^1].Line.Measures[^1].Number;
            ScoreSettings settings = score.Settings;
            DrawText(context, $"1 = {NumberScore.KeyNames[settings.Tonic]}", Margin, 82, 14, Ink, Regular);
            DrawMeter(context, settings.BeatsPerMeasure, Margin + 74, 74);
            string meta = FormattableString.Invariant($"{settings.Bpm:0.##} BPM    第 {first}-{last} 小节");
            DrawText(context, meta, Margin + 105, 82, 12, Muted, Regular);

            double y = ScoreTop;
            foreach (var row in rows)
            {
                context.PushTransform(new TranslateTransform(Margin + ScoreWidth * (1 - row.Scale) / 2, y));
                context.PushTransform(new ScaleTransform(row.Scale, row.Scale));
                ScoreLineView.DrawLine(context, row.Line, new Size(ScoreWidth, row.OriginalHeight),
                    double.NegativeInfinity, scale * row.Scale);
                context.Pop();
                context.Pop();
                y += row.Height;
            }

            double footer = Height - 39;
            DrawText(context, "0 休止  ·  一 / 两条下划线为八分 / 十六分音符  ·  短横延一拍  ·  上下点表示八度",
                Margin, footer, 11, Muted, Regular);
            DrawText(context, $"{page} / {total}", Width - Margin - 75, footer, 12, Ink, Bold, 75, TextAlignment.Right);
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Width * scale),
            (int)Math.Ceiling(Height * scale), Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var png = new MemoryStream();
        encoder.Save(png);
        png.Position = 0;
        return png;
    }

    /// <summary>按传统简谱把拍号的分子、横线和分母竖向排列。</summary>
    private static void DrawMeter(DrawingContext context, int beats, double x, double y)
    {
        DrawText(context, beats.ToString(CultureInfo.InvariantCulture), x + 2, y, 11, Ink, Bold);
        context.DrawLine(new Pen(Ink, .8), new Point(x, y + 14), new Point(x + 12, y + 14));
        DrawText(context, "4", x + 2, y + 14, 11, Ink, Bold);
    }

    private static void DrawText(DrawingContext context, string value, double x, double y, double fontSize,
        Brush brush, Typeface face, double? maximumWidth = null, TextAlignment alignment = TextAlignment.Left)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            face, fontSize, brush, Dpi / 96);
        if (maximumWidth is double width)
        {
            text.MaxTextWidth = width;
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
            text.TextAlignment = alignment;
        }
        context.DrawText(text, new Point(x, y));
    }

    private static Brush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
