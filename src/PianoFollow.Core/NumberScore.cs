using System.Globalization;
using System.Text;

namespace PianoFollow.Core;

/// <summary>谱面参数。首拍偏移按原音频秒数计，分母固定为四分音符。</summary>
public sealed record ScoreSettings(int Tonic, double Bpm, int BeatsPerMeasure, double Downbeat);

/// <summary>用于谱面排列的音符片段，长度及位置以十六分音符为单位。</summary>
public sealed record ScoreGlyph(PianoNote? Note, int Tick, int Length, int Lane, bool TieIn, bool TieOut);

/// <summary>从中央 C 划分的两个音区，包含并行声部和休止。</summary>
public sealed record ScoreTrack(IReadOnlyList<ScoreGlyph> Glyphs, int LaneCount);

/// <summary>单小节的编号、在音频中的起点和两个音区。</summary>
public sealed record ScoreMeasure(int Number, double Start, ScoreTrack Upper, ScoreTrack Lower);

/// <summary>可跟随和导出的简谱。节拍整理不会改动原音符的时间戳。</summary>
public sealed class NumberScore
{
    private static readonly string[] Keys = ["C", "♯C", "D", "♭E", "E", "F", "♯F", "G", "♭A", "A", "♭B", "B"];
    private static readonly int[] Degrees = [1, 1, 2, 3, 3, 4, 4, 5, 6, 6, 7, 7];
    private static readonly string[] Accidentals = ["", "♯", "", "♭", "", "", "♯", "", "♭", "", "♭", ""];
    private static readonly int[] Durations = [16, 12, 8, 6, 4, 3, 2, 1];
    private const int TicksPerBeat = 4;
    private const int MiddleC = 60;

    private NumberScore(ScoreSettings settings, double origin, IReadOnlyList<ScoreMeasure> measures)
    {
        Settings = settings;
        Origin = origin;
        Measures = measures;
    }

    /// <summary>谱面调号、拍速、拍号和用户输入的首拍偏移。</summary>
    public ScoreSettings Settings { get; }

    /// <summary>第一个显示小节相对音频起点的秒数。为保留弱起，可能为负。</summary>
    public double Origin { get; }

    /// <summary>覆盖音频时间轴的有序小节。</summary>
    public IReadOnlyList<ScoreMeasure> Measures { get; }

    /// <summary>一拍为四个格，每格时长以秒计。</summary>
    public double TickSeconds => 60 / Settings.Bpm / TicksPerBeat;

    /// <summary>一个完整小节的十六分音符格数。</summary>
    public int MeasureTicks => Settings.BeatsPerMeasure * TicksPerBeat;

    /// <summary>根据播放秒数取得零基小节索引。</summary>
    public int MeasureAt(double position)
        => Math.Clamp((int)Math.Floor((position - Origin) / (MeasureTicks * TickSeconds)), 0, Measures.Count - 1);

    /// <summary>可选择的首调主音名称，顺序为 C 至 B 的十二音。</summary>
    public static IReadOnlyList<string> KeyNames => Keys;

    /// <summary>将 MIDI 音高换算为级数、升降号和相对参考主音的八度。</summary>
    public static (string Symbol, int Octave) Describe(int midi, int tonic)
    {
        int delta = midi - (MiddleC + tonic);
        int octave = (int)Math.Floor(delta / 12.0);
        int chromatic = delta - octave * 12;
        return (Accidentals[chromatic] + Degrees[chromatic], octave);
    }

    /// <summary>估计可编辑的四分音符速度和节拍对齐位置。</summary>
    public static (double Bpm, double Downbeat) EstimatePulse(IReadOnlyList<PianoNote> notes)
    {
        var onsets = new List<double>();
        foreach (var note in notes)
        {
            if (onsets.Count == 0 || note.Start - onsets[^1] >= 0.055)
                onsets.Add(note.Start);
            if (onsets.Count == 320)
                break;
        }

        if (onsets.Count < 6 || onsets[^1] - onsets[0] < 1.7)
            return (100, 0);

        double best = double.NegativeInfinity;
        double bestBpm = 100;
        double bestPhase = 0;
        for (double bpm = 50; bpm <= 200; bpm += 0.25)
        {
            double beat = 60 / bpm;
            double real = 0, imaginary = 0, halfReal = 0, halfImaginary = 0;
            foreach (double time in onsets)
            {
                double phase = time / beat * Math.Tau;
                var (sin, cos) = Math.SinCos(phase);
                real += cos;
                imaginary += sin;
                halfReal += cos * cos - sin * sin;
                halfImaginary += 2 * cos * sin;
            }
            double score = (0.75 * Math.Sqrt(real * real + imaginary * imaginary)
                + 0.25 * Math.Sqrt(halfReal * halfReal + halfImaginary * halfImaginary)) / onsets.Count
                - 0.12 * Math.Abs(Math.Log2(bpm / 105));
            if (score <= best)
                continue;
            best = score;
            bestBpm = bpm;
            bestPhase = Math.Atan2(imaginary, real) / Math.Tau * beat;
        }

        return (bestBpm, Math.Round(Math.Abs(bestPhase) < 0.025 ? 0 : bestPhase, 3));
    }

    /// <summary>将原音符量化为十六分格并按小节拆分延音，保留复音的独立时值。</summary>
    public static NumberScore Create(IReadOnlyList<PianoNote> notes, double duration, ScoreSettings settings)
    {
        if (settings.Tonic is < 0 or > 11 || !double.IsFinite(settings.Bpm)
            || settings.Bpm is < 40 or > 220 || settings.BeatsPerMeasure is < 2 or > 4
            || !double.IsFinite(settings.Downbeat) || Math.Abs(settings.Downbeat) > 10)
            throw new ArgumentOutOfRangeException(nameof(settings), "速度范围为 40–220，首拍偏移为 -10–10 秒。");
        if (!double.IsFinite(duration) || duration <= 0 || duration > AudioInput.MaximumMinutes * 60)
            throw new ArgumentOutOfRangeException(nameof(duration));

        double step = 60 / settings.Bpm / TicksPerBeat;
        int bar = settings.BeatsPerMeasure * TicksPerBeat;
        double barSeconds = step * bar;
        double firstNote = notes.Where(n => double.IsFinite(n.Start) && n.Start >= 0 && n.Start < duration)
            .Select(n => n.Start).DefaultIfEmpty(0).Min();
        double origin = settings.Downbeat
            - Math.Ceiling(Math.Max(0, settings.Downbeat - firstNote) / barSeconds) * barSeconds;
        int count = Math.Max(1, (int)Math.Ceiling((duration - origin) / barSeconds));
        var upper = Enumerable.Range(0, count).Select(_ => new List<ScoreGlyph>()).ToArray();
        var lower = Enumerable.Range(0, count).Select(_ => new List<ScoreGlyph>()).ToArray();

        foreach (var note in notes)
        {
            if (note.Midi is < 21 or > 108 || !double.IsFinite(note.Start) || !double.IsFinite(note.End)
                || note.Start < 0 || note.End <= note.Start || note.Start >= duration)
                continue;
            int tick = Math.Clamp(Quantize(note.Start, origin, step), 0, count * bar - 1);
            int end = Math.Clamp(Quantize(note.End, origin, step), tick + 1, count * bar);
            int first = tick;
            var track = note.Midi >= MiddleC ? upper : lower;
            while (tick < end)
            {
                int position = tick % bar;
                int available = Math.Min(bar - position, end - tick);
                int length = Durations.First(n => n <= available && (n <= 4 || position % 4 == 0));
                track[tick / bar].Add(new ScoreGlyph(note, position, length, 0, tick > first, tick + length < end));
                tick += length;
            }
        }

        var measures = new List<ScoreMeasure>(count);
        for (int i = 0; i < count; i++)
        {
            measures.Add(new ScoreMeasure(i + 1, origin + i * barSeconds,
                BuildTrack(upper[i], bar), BuildTrack(lower[i], bar)));
        }
        return new NumberScore(settings, origin, measures);
    }

    private static int Quantize(double seconds, double origin, double step)
        => (int)Math.Round((seconds - origin) / step, MidpointRounding.AwayFromZero);

    private static ScoreTrack BuildTrack(List<ScoreGlyph> notes, int ticks)
    {
        var usedUntil = new List<int>();
        bool[] occupied = new bool[ticks];
        var events = new List<ScoreGlyph>();
        var lanes = new Dictionary<PianoNote, int>();
        foreach (var glyph in notes.OrderBy(n => n.Tick).ThenByDescending(n => n.Note!.Midi))
        {
            int lane = lanes.TryGetValue(glyph.Note!, out int previous) && usedUntil[previous] <= glyph.Tick
                ? previous : usedUntil.FindIndex(end => end <= glyph.Tick);
            if (lane < 0)
            {
                lane = usedUntil.Count;
                usedUntil.Add(0);
            }
            usedUntil[lane] = glyph.Tick + glyph.Length;
            if (glyph.TieOut)
                lanes[glyph.Note!] = lane;
            events.Add(glyph with { Lane = lane });
            Array.Fill(occupied, true, glyph.Tick, glyph.Length);
        }

        int tick = 0;
        while (tick < ticks)
        {
            if (occupied[tick])
            {
                tick++;
                continue;
            }
            int until = tick + 1;
            while (until < ticks && !occupied[until])
                until++;
            while (tick < until)
            {
                int length = Durations.First(n => n <= 4 && n <= until - tick && n <= 4 - tick % 4);
                events.Add(new ScoreGlyph(null, tick, length, 0, false, false));
                tick += length;
            }
        }
        return new ScoreTrack(events, Math.Max(1, usedUntil.Count));
    }

    /// <summary>导出含小节时间、起拍位置和每个音独立时值的 UTF-8 文本。</summary>
    public string ToText(string title)
    {
        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine(FormattableString.Invariant(
            $"1={Keys[Settings.Tonic]}4  {Settings.BeatsPerMeasure}/4  BPM {Settings.Bpm:0.##}  首拍偏移 {Settings.Downbeat:0.###} 秒"));
        text.AppendLine("每组格式为「小节内拍位:音符(时值拍数)」，从第 1 拍开始。同拍各音用 + 连接。");
        text.AppendLine("0 为休止，' 升八度，, 降八度，~ 表示同音延续。高低音区以中央 C 划分。");
        foreach (var measure in Measures)
        {
            text.AppendLine();
            text.AppendLine(FormattableString.Invariant($"第 {measure.Number:000} 小节  {Math.Max(0, measure.Start):0.000} 秒"));
            AppendTrack(text, "高音", measure.Upper, Settings.Tonic);
            AppendTrack(text, "低音", measure.Lower, Settings.Tonic);
        }
        return text.ToString();
    }

    private static void AppendTrack(StringBuilder builder, string label, ScoreTrack track, int tonic)
    {
        builder.Append(label).Append(" | ");
        foreach (var group in track.Glyphs.OrderBy(g => g.Tick).GroupBy(g => g.Tick))
        {
            builder.Append((1 + group.Key / 4.0).ToString("0.##", CultureInfo.InvariantCulture)).Append(':');
            builder.Append(string.Join("+", group.Select(g =>
            {
                var (symbol, octave) = g.Note is null ? ("0", 0) : Describe(g.Note.Midi, tonic);
                return FormattableString.Invariant($"{(g.TieIn ? "~" : "")}{symbol}{new string(octave > 0 ? '\'' : ',', Math.Abs(octave))}({g.Length / 4.0:0.##})");
            })));
            builder.Append("  ");
        }
        builder.AppendLine("|");
    }
}
