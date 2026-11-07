using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PianoFollow.Core;

namespace PianoFollow;

/// <summary>可虚拟化的谱行。末行不足列时，小节仍使用整行的列宽。</summary>
/// <param name="Score">该谱行所属的完整简谱。</param>
/// <param name="Measures">谱行内的小节。</param>
/// <param name="Columns">包含末行空位在内的总列数。</param>
/// <param name="Scale">屏幕上的缩放倍率，打印默认使用原尺寸。</param>
public sealed record ScoreLine(NumberScore Score, IReadOnlyList<ScoreMeasure> Measures, int Columns, double Scale = 1);

/// <summary>点击谱面请求跳转到给定的音频秒数。</summary>
public sealed class ScoreSeekEventArgs(double seconds) : EventArgs
{
    public double Seconds { get; } = seconds;
}

/// <summary>绘制数字简谱、复音声部及随音频移动的光标。</summary>
public sealed class ScoreLineView : FrameworkElement
{
    private const double Left = 54;
    private const double Right = 14;
    private const double Row = 25;
    private const double Header = 30;
    private const double TrackGap = 8;
    private const double Bottom = 10;
    private const int MaximumColumns = 6;

    private static readonly Brush Ink = Brushes.Black;
    private static readonly Brush Muted = BrushFrom("#5A5A5A");
    private static readonly Brush ActiveFill = BrushFrom("#DDEFE8");
    private static readonly Pen MusicLine = MakePen("#171717", 0.9);
    private static readonly Pen FinalLine = MakePen("#171717", 2.2);
    private static readonly Pen Pointer = MakePen("#81A99A", 1);
    private static readonly Typeface TextFace = new("Microsoft YaHei UI");
    private static readonly Typeface NumberFace = new(new FontFamily("Arial"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>谱行内容的 WPF 绑定属性。</summary>
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(nameof(Line),
        typeof(ScoreLine), typeof(ScoreLineView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure
            | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>原音频播放位置的 WPF 绑定属性，以秒为单位。</summary>
    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(nameof(Playhead),
        typeof(double), typeof(ScoreLineView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public ScoreLine? Line
    {
        get => (ScoreLine?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public double Playhead
    {
        get => (double)GetValue(PlayheadProperty);
        set => SetValue(PlayheadProperty, value);
    }

    /// <summary>用户点击小节时发出，位置按谱面拍位计算。</summary>
    public event EventHandler<ScoreSeekEventArgs>? SeekRequested;

    /// <summary>按可用宽度与拍号选列数，为每个十六分格至少预留 19 个设备无关像素。</summary>
    internal static int ColumnsForWidth(double width, int measureTicks)
    {
        double minimumBar = Math.Max(200, measureTicks * 19);
        return Math.Clamp((int)Math.Floor((width - Left - Right) / minimumBar), 1, MaximumColumns);
    }

    /// <summary>计算谱行所需高度，单位为设备无关像素。</summary>
    internal static double LineHeight(ScoreLine line)
        => Header + (line.Measures.Max(m => m.Upper.LaneCount) + line.Measures.Max(m => m.Lower.LaneCount))
            * Row + TrackGap + Bottom;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Line is not { } line)
            return new Size(0, 0);
        double width = double.IsInfinity(availableSize.Width) ? 860 : availableSize.Width;
        return new Size(width, LineHeight(line) * line.Scale);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (Line is not { } line)
            return;

        double scale = line.Scale;
        context.PushTransform(new ScaleTransform(scale, scale));
        DrawLine(context, line, new Size(RenderSize.Width / scale, RenderSize.Height / scale),
            Playhead, VisualTreeHelper.GetDpi(this).PixelsPerDip * scale);
        context.Pop();
    }

    /// <summary>以传统数字简谱绘制谱行。导出时传入负无穷，省略播放指示。</summary>
    internal static void DrawLine(DrawingContext context, ScoreLine line, Size size, double playhead, double pixelsPerDip)
    {
        if (size.Width <= Left + Right)
            return;
        double barWidth = BarWidth(line, size.Width);
        double cell = barWidth / line.Score.MeasureTicks;
        double upperBottom = Header + line.Measures.Max(m => m.Upper.LaneCount) * Row;
        double lowTop = upperBottom + TrackGap;
        context.DrawRectangle(Brushes.White, null, new Rect(size));
        DrawText(context, "高音", 18, Header + 3, 11, Muted, pixelsPerDip);
        DrawText(context, "低音", 18, lowTop + 3, 11, Muted, pixelsPerDip);

        for (int i = 0; i < line.Measures.Count; i++)
        {
            ScoreMeasure measure = line.Measures[i];
            double left = Left + i * barWidth;
            double right = left + barWidth;
            double playbackTick = (playhead - measure.Start) / line.Score.TickSeconds;
            bool current = playbackTick >= 0 && playbackTick < line.Score.MeasureTicks;

            DrawText(context, $"第 {measure.Number} 小节", left + 5, 8, 11, current ? Ink : Muted, pixelsPerDip);
            DrawText(context, FormatTime(measure.Start), right - 37, 9, 10, Muted, pixelsPerDip);
            if (current)
            {
                double x = left + playbackTick * cell;
                context.DrawLine(Pointer, new Point(x, Header - 5), new Point(x, size.Height - Bottom));
            }

            bool final = measure.Number == line.Score.Measures.Count;
            DrawBar(context, right, Header, upperBottom, final);
            DrawBar(context, right, lowTop, size.Height - Bottom, final);
            DrawTrack(context, line.Score, measure.Upper, left, Header, cell, playhead, playbackTick, pixelsPerDip);
            DrawTrack(context, line.Score, measure.Lower, left, lowTop, cell, playhead, playbackTick, pixelsPerDip);
        }
    }

    /// <summary>各音区只画小节线，曲终以细线和粗线组成终止线。</summary>
    private static void DrawBar(DrawingContext context, double x, double top, double bottom, bool final)
    {
        context.DrawLine(final ? FinalLine : MusicLine, new Point(x, top + 3), new Point(x, bottom - 3));
        if (final)
            context.DrawLine(MusicLine, new Point(x - 4, top + 3), new Point(x - 4, bottom - 3));
    }

    /// <summary>逐声部绘制音符，按拍成组减时线，并连接同一音的分段延音。</summary>
    private static void DrawTrack(DrawingContext context, NumberScore score, ScoreTrack track, double left,
        double top, double cell, double playhead, double playbackTick, double pixelsPerDip)
    {
        foreach (var lane in track.Glyphs.GroupBy(g => g.Lane))
        {
            ScoreGlyph[] glyphs = lane.OrderBy(g => g.Tick).ToArray();
            double y = top + lane.Key * Row;
            for (int i = 0; i < glyphs.Length; i++)
            {
                ScoreGlyph glyph = glyphs[i];
                double x = NoteX(left, cell, glyph.Tick);
                bool active = glyph.Note is { } note && playhead >= note.Start && playhead < note.End
                    && playbackTick >= glyph.Tick - .5 && playbackTick < glyph.Tick + glyph.Length + .5;
                DrawGlyph(context, score, glyph, x, y, cell, active, pixelsPerDip);

                if (glyph.TieIn && (i == 0 || !Continues(glyphs[i - 1], glyph)))
                    DrawTie(context, Math.Max(left + 1, x - cell * .8), x - 4, y + 4);
                if (!glyph.TieOut)
                    continue;

                double end = i + 1 < glyphs.Length && Continues(glyph, glyphs[i + 1])
                    ? NoteX(left, cell, glyphs[i + 1].Tick) - 4
                    : left + (glyph.Tick + glyph.Length) * cell - 1;
                DrawTie(context, x + 4, end, y + 4);
            }
            DrawBeams(context, glyphs, left, y, cell);
        }
    }

    /// <summary>绘制一个数字及其临时升降号、附点、八度点和延长横线。</summary>
    private static void DrawGlyph(DrawingContext context, NumberScore score, ScoreGlyph glyph,
        double x, double y, double cell, bool active, double pixelsPerDip)
    {
        if (active)
            context.DrawRoundedRectangle(ActiveFill, null,
                new Rect(x - Math.Min(10, cell / 2), y - 1, Math.Min(20, cell), Row), 3, 3);

        var (symbol, octave) = glyph.Note is null ? ("0", 0) : NumberScore.Describe(glyph.Note.Midi, score.Settings.Tonic);
        var number = MakeText(symbol[^1..], 15, Ink, NumberFace, pixelsPerDip);
        double digitLeft = x - number.Width / 2;
        context.DrawText(number, new Point(digitLeft, y + 3));
        if (symbol.Length > 1)
        {
            var accidental = MakeText(symbol[..^1], 11, Ink, TextFace, pixelsPerDip);
            context.DrawText(accidental, new Point(digitLeft - accidental.Width - 1, y + 3));
        }
        if (glyph.Length is 3 or 6)
            context.DrawEllipse(Ink, null, new Point(x + Math.Min(8, cell / 2 - 1), y + 12), 1.15, 1.15);

        int beams = BeamCount(glyph);
        for (int j = 0; j < Math.Abs(octave); j++)
        {
            double dotY = octave > 0 ? y + 1 - j * 1.9 : y + 18.9 + beams * 1.8 + j * 1.75;
            context.DrawEllipse(Ink, null, new Point(x, dotY), .95, .95);
        }
        for (int t = 4; glyph.Length >= 8 && t < glyph.Length; t += 4)
        {
            double dash = x + t * cell;
            context.DrawLine(MusicLine, new Point(dash - 4, y + 12), new Point(dash + 4, y + 12));
        }
    }

    /// <summary>同拍、相邻且减时级别相同的符号共用下划线，分拍处断开。</summary>
    private static void DrawBeams(DrawingContext context, ScoreGlyph[] glyphs, double left, double top, double cell)
    {
        for (int level = 1; level <= 2; level++)
        {
            double y = top + 18 + (level - 1) * 1.9;
            for (int first = 0; first < glyphs.Length; first++)
            {
                if (BeamCount(glyphs[first]) < level)
                    continue;
                int last = first;
                while (last + 1 < glyphs.Length && BeamCount(glyphs[last + 1]) >= level
                    && glyphs[last + 1].Tick / 4 == glyphs[first].Tick / 4
                    && glyphs[last].Tick + glyphs[last].Length == glyphs[last + 1].Tick)
                    last++;

                context.DrawLine(MusicLine, new Point(NoteX(left, cell, glyphs[first].Tick) - 4.5, y),
                    new Point(NoteX(left, cell, glyphs[last].Tick) + 4.5, y));
                first = last;
            }
        }
    }

    private static bool Continues(ScoreGlyph first, ScoreGlyph second) => first.TieOut && second.TieIn
        && ReferenceEquals(first.Note, second.Note) && first.Tick + first.Length == second.Tick;

    private static int BeamCount(ScoreGlyph glyph) => glyph.Length switch { 1 => 2, 2 or 3 => 1, _ => 0 };

    private static double NoteX(double left, double cell, int tick) => left + (tick + .5) * cell;

    private static void DrawTie(DrawingContext context, double start, double end, double y)
    {
        if (end <= start + 1)
            return;
        var tie = new StreamGeometry();
        using (var path = tie.Open())
        {
            path.BeginFigure(new Point(start, y), false, false);
            path.QuadraticBezierTo(new Point((start + end) / 2, y - 6), new Point(end, y), true, false);
        }
        context.DrawGeometry(null, MusicLine, tie);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (TimeAt(e.GetPosition(this).X) is not double seconds)
            return;
        SeekRequested?.Invoke(this, new ScoreSeekEventArgs(seconds));
        e.Handled = true;
    }

    /// <summary>用谱面的实际列宽把点击横坐标换算为音频秒数，空列不响应。</summary>
    internal double? TimeAt(double x)
    {
        if (Line is not { } line || ActualWidth <= (Left + Right) * line.Scale)
            return null;
        double column = (x / line.Scale - Left) / BarWidth(line, ActualWidth / line.Scale);
        int index = (int)Math.Floor(column);
        if (index < 0 || index >= line.Measures.Count)
            return null;
        return line.Measures[index].Start
            + (column - index) * line.Score.MeasureTicks * line.Score.TickSeconds;
    }

    private static double BarWidth(ScoreLine line, double width) => (width - Left - Right) / line.Columns;

    private static void DrawText(DrawingContext context, string value, double x, double y, double size,
        Brush brush, double pixelsPerDip)
        => context.DrawText(MakeText(value, size, brush, TextFace, pixelsPerDip), new Point(x, y));

    private static FormattedText MakeText(string value, double size, Brush brush, Typeface face, double pixelsPerDip)
        => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, pixelsPerDip);

    private static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static Brush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(string hex, double width)
    {
        var pen = new Pen(BrushFrom(hex), width);
        pen.Freeze();
        return pen;
    }
}
