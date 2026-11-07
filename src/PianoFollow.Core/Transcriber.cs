using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PianoFollow.Core;

/// <summary>一个实音事件，包含 MIDI 音高和相对音频起点的起止秒数。</summary>
public sealed record PianoNote(int Midi, double Start, double End);

/// <summary>使用内嵌的 Basic Pitch 模型，从本地音频提取复音钢琴音符。</summary>
public static class Transcriber
{
    private const int WindowSamples = 43_844;
    private const int FrameHop = 256;
    private const int EdgeFrames = 15;
    private const int ModelFrames = 172;
    private const int RetainedFrames = ModelFrames - EdgeFrames * 2;
    private const int Stride = WindowSamples - EdgeFrames * 2 * FrameHop;
    private const int PitchCount = 88;
    private const int LowestMidi = 21;
    private const float OnsetThreshold = 0.50f;
    private const float SustainThreshold = 0.30f;
    private const int MinimumFrames = 6;
    private const int MaximumGap = 5;

    // 模型置信度用于区分同时起音的真实高音与较弱的整数倍泛音。
    private sealed record Candidate(PianoNote Note, float Peak, float Attack);

    /// <summary>返回按开始时间排序的实音，进度范围为 0 到 1。</summary>
    public static IReadOnlyList<PianoNote> Recognize(
        AudioInput audio, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (audio.Samples.Length == 0 || audio.Samples.All(s => Math.Abs(s) < 0.0005f))
            return [];

        using var modelStream = typeof(Transcriber).Assembly.GetManifestResourceStream(
            "PianoFollow.Core.Model.nmp.onnx")
            ?? throw new InvalidOperationException("程序资源中缺少识别模型。");
        using var model = new MemoryStream();
        modelStream.CopyTo(model);
        using var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1
        };
        using var session = new InferenceSession(model.ToArray(), options);
        int windowCount = (audio.Samples.Length + Stride - 1) / Stride;
        var sustain = new float[windowCount * RetainedFrames * PitchCount];
        var onset = new float[sustain.Length];
        var window = new float[WindowSamples];
        var tensor = new DenseTensor<float>(window, [1, WindowSamples, 1]);
        NamedOnnxValue[] inputs = [NamedOnnxValue.CreateFromTensor("serving_default_input_2:0", tensor)];
        string[] outputs = ["StatefulPartitionedCall:1", "StatefulPartitionedCall:2"];

        for (int i = 0; i < windowCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(window);
            int start = i * Stride - EdgeFrames * FrameHop;
            int readFrom = Math.Max(0, start);
            int writeAt = readFrom - start;
            int count = Math.Min(WindowSamples - writeAt, audio.Samples.Length - readFrom);
            Array.Copy(audio.Samples, readFrom, window, writeAt, count);

            using var result = session.Run(inputs, outputs);
            int destination = i * RetainedFrames * PitchCount;
            CopyCenter(result.First(x => x.Name == outputs[0]).AsTensor<float>(), sustain, destination);
            CopyCenter(result.First(x => x.Name == outputs[1]).AsTensor<float>(), onset, destination);
            progress?.Report((i + 1) / (double)windowCount);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Extract(sustain, onset, audio.Duration, cancellationToken);
    }

    private static void CopyCenter(Tensor<float> tensor, float[] target, int destination)
    {
        int count = RetainedFrames * PitchCount;
        int start = EdgeFrames * PitchCount;
        if (tensor is DenseTensor<float> dense)
            dense.Buffer.Span.Slice(start, count).CopyTo(target.AsSpan(destination, count));
        else
            tensor.ToArray().AsSpan(start, count).CopyTo(target.AsSpan(destination, count));
    }

    private static IReadOnlyList<PianoNote> Extract(float[] sustain, float[] onset, double duration, CancellationToken token)
    {
        int frames = sustain.Length / PitchCount;
        var notes = new List<Candidate>();

        for (int pitch = 0; pitch < PitchCount; pitch++)
        {
            token.ThrowIfCancellationRequested();
            int start = -1;
            int lastActive = -1;
            float peak = 0;
            float attack = 0;

            void Finish(int end)
            {
                if (end - start >= MinimumFrames && (peak >= 0.44f || attack >= 0.60f))
                {
                    double from = FrameTime(start);
                    double to = Math.Min(duration, FrameTime(end));
                    if (to - from >= 0.052)
                        notes.Add(new Candidate(new PianoNote(pitch + LowestMidi, from, to), peak, attack));
                }
                start = -1;
                lastActive = -1;
                peak = 0;
                attack = 0;
            }

            for (int frame = 0; frame < frames && FrameTime(frame) < duration; frame++)
            {
                int index = frame * PitchCount + pitch;
                float hold = sustain[index];
                float hit = onset[index];
                bool starts = hit >= OnsetThreshold
                    && (frame == 0 || hit > onset[index - PitchCount])
                    && (frame + 1 == frames || hit >= onset[index + PitchCount]);

                if (start >= 0 && starts && frame - start >= MinimumFrames)
                    Finish(frame - lastActive > MaximumGap ? lastActive + 1 : frame);

                if (start < 0 && (starts || hold >= SustainThreshold))
                {
                    start = frame;
                    lastActive = frame;
                }

                if (start < 0)
                    continue;
                peak = Math.Max(peak, hold);
                attack = Math.Max(attack, hit);
                if (hold >= SustainThreshold)
                    lastActive = frame;
                else if (frame - lastActive > MaximumGap)
                    Finish(lastActive + 1);
            }

            if (start >= 0)
                Finish(lastActive + 1);
        }

        var ordered = notes.OrderBy(x => x.Note.Start).ThenBy(x => x.Note.Midi).ToArray();
        var filtered = new List<PianoNote>(ordered.Length);
        int first = 0;
        foreach (var candidate in ordered)
        {
            const double onsetTolerance = 0.055;
            while (ordered[first].Note.Start < candidate.Note.Start - onsetTolerance)
                first++;

            bool harmonic = false;
            for (int i = first; i < ordered.Length && ordered[i].Note.Start <= candidate.Note.Start + onsetTolerance; i++)
            {
                Candidate lower = ordered[i];
                int interval = candidate.Note.Midi - lower.Note.Midi;
                if (interval is (12 or 19 or 24 or 28 or 31)
                    && lower.Attack >= 0.73f
                    && candidate.Attack < lower.Attack * 0.83f
                    && candidate.Peak < lower.Peak * 0.88f)
                {
                    harmonic = true;
                    break;
                }
            }
            if (!harmonic)
                filtered.Add(candidate.Note);
        }
        return filtered;
    }

    /// <summary>换算重叠窗口裁剪后的帧，避免长曲随窗口数累积时间偏差。</summary>
    private static double FrameTime(int frame)
        => (frame / RetainedFrames * Stride + frame % RetainedFrames * FrameHop)
            / (double)AudioInput.ModelSampleRate;
}
