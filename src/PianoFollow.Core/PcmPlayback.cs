using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;

namespace PianoFollow.Core;

/// <summary>通过 Windows PCM 设备播放，并将设备实际输出字节换算为原音频位置。</summary>
public sealed class PcmPlayback(AudioInput audio) : IDisposable
{
    private WaveOutEvent? _device;
    private WaveFileReader? _reader;
    private double _anchor;
    private double _speed = 1;
    private bool _muted;
    private bool _disposed;

    /// <summary>自然结束时传 null，设备失败时传实际异常。</summary>
    public event Action<Exception?>? Completed;

    /// <summary>当前在原音频中的位置，以秒为单位。</summary>
    public double Position => _device is null
        ? _anchor
        : Math.Min(audio.Duration, _anchor
            + _device.GetPosition() * _speed / _device.OutputWaveFormat.AverageBytesPerSecond);

    /// <summary>相对原速的播放倍率，变化时从当前音频位置继续。</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            if (!double.IsFinite(value) || value is < 0.5 or > 1.25)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (value == _speed)
                return;
            bool resume = _device?.PlaybackState == PlaybackState.Playing;
            _anchor = Position;
            ReleaseDevice();
            _speed = value;
            if (resume)
                Play();
        }
    }

    /// <summary>静音只影响设备输出，音频时钟照常运行。</summary>
    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_device is not null)
                _device.Volume = value ? 0 : .9f;
        }
    }

    /// <summary>从当前时间继续播放，设备会在实际播放时打开。</summary>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_anchor >= audio.Duration)
            _anchor = 0;
        if (_device is null)
            OpenDevice();
        _device!.Play();
    }

    /// <summary>暂停输出并保留设备位置与已缓冲音频。</summary>
    public void Pause() => _device?.Pause();

    /// <summary>定位到原音频秒数，并恢复定位前的播放或暂停状态。</summary>
    public void Seek(double seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool resume = _device?.PlaybackState == PlaybackState.Playing;
        ReleaseDevice();
        _anchor = Math.Clamp(seconds, 0, audio.Duration);
        if (resume && _anchor < audio.Duration)
            Play();
    }

    private void OpenDevice()
    {
        var reader = new WaveFileReader(audio.PlaybackPath);
        var device = new WaveOutEvent { DesiredLatency = 100 };
        try
        {
            int alignment = reader.WaveFormat.BlockAlign;
            reader.Position = (long)(_anchor * reader.WaveFormat.AverageBytesPerSecond) / alignment * alignment;
            if (_speed == 1)
                device.Init(reader);
            else
                device.Init(new SampleToWaveProvider16(new TempoProvider(reader.ToSampleProvider(), _speed)));
            device.Volume = _muted ? 0 : .9f;
            device.PlaybackStopped += (_, e) => DeviceStopped(device, e.Exception);
            _reader = reader;
            _device = device;
        }
        catch
        {
            device.Dispose();
            reader.Dispose();
            throw;
        }
    }

    private void DeviceStopped(WaveOutEvent device, Exception? error)
    {
        if (!ReferenceEquals(device, _device))
            return;
        if (error is null)
            _anchor = audio.Duration;
        ReleaseDevice();
        Completed?.Invoke(error);
    }

    private void ReleaseDevice()
    {
        WaveOutEvent? device = _device;
        _device = null;
        device?.Dispose();
        _reader?.Dispose();
        _reader = null;
    }

    /// <summary>关闭设备并释放临时 WAV 的读取句柄。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseDevice();
    }

    /// <summary>以固定原音高改变速度，按需向输出设备提供交错立体声样本。</summary>
    private sealed class TempoProvider(ISampleProvider input, double speed) : ISampleProvider
    {
        private readonly SoundTouchProcessor _tempo = new()
        {
            SampleRate = input.WaveFormat.SampleRate,
            Channels = input.WaveFormat.Channels,
            Tempo = speed
        };

        private readonly float[] _buffer = new float[4096 * input.WaveFormat.Channels];
        private bool _flushed;

        /// <summary>保持源采样率和声道数，速度只由时间拉伸改变。</summary>
        public WaveFormat WaveFormat => input.WaveFormat;

        /// <summary>读取至多 count 个交错样本，流结束时返回 0。</summary>
        public int Read(float[] buffer, int offset, int count)
        {
            int channels = WaveFormat.Channels;
            int filled = 0;
            while (filled + channels <= count)
            {
                int frames = _tempo.ReceiveSamples(buffer.AsSpan(offset + filled, count - filled),
                    (count - filled) / channels);
                filled += frames * channels;
                if (frames != 0)
                    continue;
                if (_flushed)
                    break;
                int read = input.Read(_buffer, 0, _buffer.Length);
                if (read > 0)
                    _tempo.PutSamples(_buffer, read / channels);
                else
                {
                    _tempo.Flush();
                    _flushed = true;
                }
            }
            return filled;
        }
    }
}
