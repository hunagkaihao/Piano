using NAudio.Wave;

namespace PianoFollow.Core;

/// <summary>保留立体声播放文件，并提供识别模型所需的单声道采样。</summary>
public sealed class AudioInput : IDisposable
{
    public const int ModelSampleRate = 22_050;
    public const int MaximumMinutes = 15;
    private const int PlaybackRate = 44_100;
    private bool _disposed;

    private AudioInput(string path, float[] samples, double duration)
    {
        PlaybackPath = path;
        Samples = samples;
        Duration = duration;
    }

    /// <summary>本地临时 WAV 的绝对路径，供播放器读取。</summary>
    public string PlaybackPath { get; }

    /// <summary>22,050 Hz、取值约为 -1 到 1 的单声道采样。</summary>
    public float[] Samples { get; }

    /// <summary>播放文件时长，单位为秒。</summary>
    public double Duration { get; }

    /// <summary>解码音频并写出与识别时间轴一致的临时播放文件。</summary>
    public static AudioInput Decode(string source, CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetTempPath(), $"PianoFollow-{Guid.NewGuid():N}.wav");
        try
        {
            using WaveStream input = Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                ? new WaveFileReader(source)
                : new MediaFoundationReader(source);
            if (input.TotalTime > TimeSpan.FromMinutes(MaximumMinutes))
                throw new InvalidDataException($"请选择 {MaximumMinutes} 分钟以内的音频。");

            var stereo = new WaveFormat(PlaybackRate, 16, 2);
            using var converter = new MediaFoundationResampler(input, stereo) { ResamplerQuality = 60 };
            byte[] bytes = new byte[stereo.AverageBytesPerSecond];
            long totalBytes = 0;
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new WaveFileWriter(file, stereo))
            {
                int read;
                while ((read = converter.Read(bytes, 0, bytes.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalBytes += read;
                    if (totalBytes > (long)stereo.AverageBytesPerSecond * MaximumMinutes * 60)
                        throw new InvalidDataException($"请选择 {MaximumMinutes} 分钟以内的音频。");
                    writer.Write(bytes, 0, read);
                }
            }

            double duration = totalBytes / (double)stereo.AverageBytesPerSecond;
            if (duration < 0.10)
                throw new InvalidDataException("音频过短，至少需要 0.1 秒的有效音频。");

            using var playback = new WaveFileReader(path);
            using var mono = new MediaFoundationResampler(playback, new WaveFormat(ModelSampleRate, 16, 1))
            {
                ResamplerQuality = 60
            };
            var samples = new List<float>((int)Math.Ceiling(duration * ModelSampleRate));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = mono.Read(bytes, 0, bytes.Length);
                if (read == 0)
                    break;
                for (int i = 0; i + 1 < read; i += 2)
                    samples.Add((short)(bytes[i] | bytes[i + 1] << 8) / 32768f);
            }

            return new AudioInput(path, samples.ToArray(), duration);
        }
        catch
        {
            DeleteTemporaryFile(path);
            throw;
        }
    }

    /// <summary>播放器关闭后释放临时音频。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DeleteTemporaryFile(PlaybackPath);
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 媒体服务仍短暂持有文件时，应用可以继续退出。
        }
        catch (UnauthorizedAccessException)
        {
            // 系统拒绝清理时保留临时文件，不影响已经完成的转换。
        }
    }
}
