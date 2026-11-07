using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PianoFollow.Core;

namespace PianoFollow;

/// <summary>负责导入和播放，用同一媒体时间轴驱动进度条及谱面。</summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _analysis;
    private AudioInput? _audio;
    private PcmPlayback? _player;
    private IReadOnlyList<PianoNote> _notes = [];
    private NumberScore? _score;
    private (double Bpm, double Offset) _estimate;
    private string _title = "";
    private bool _playing;
    private bool _dragging;
    private bool _updatingSlider;
    private bool _closed;
    private bool _exporting;
    private int _scrolledLine = -1;
    // 当前谱行列数与滚动容器用于窗口变宽或变窄时保留阅读位置。
    private int _measuresPerLine = 1;
    // 当前谱面缩放比例，和列数一起决定何时重新排版。
    private double _scoreScale = .8;
    private ScrollViewer? _scoreScroller;
    private double _position;

    /// <summary>构建窗口并注册进度条拖动事件。</summary>
    public MainWindow()
    {
        InitializeComponent();
        TonicBox.ItemsSource = NumberScore.KeyNames;
        TonicBox.SelectedIndex = 0;
        PositionSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(StartScrub));
        PositionSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(EndScrub));
        _clock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _clock.Tick += RefreshClock;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前播放或拖动预览位置，单位为秒，供谱面绑定。</summary>
    public double Position
    {
        get => _position;
        private set
        {
            if (value == _position)
                return;
            _position = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
        }
    }

    private async void OpenAudio(object sender, RoutedEventArgs e)
    {
        if (_analysis is not null)
            return;
        var dialog = new OpenFileDialog
        {
            Title = "导入钢琴音频",
            Filter = "音频文件|*.wav;*.mp3;*.flac;*.m4a;*.aac;*.wma;*.aif;*.aiff|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            await ImportAudioAsync(dialog.FileName);
    }

    /// <summary>导入指定路径，完成解码和识别后才切换到新的谱面。</summary>
    internal async Task ImportAudioAsync(string path)
    {
        if (_analysis is not null || _closed)
            return;
        TryPlayback(Pause);
        using var request = new CancellationTokenSource();
        _analysis = request;
        SetBusy(true);
        AnalysisProgress.Value = 0;
        StatusText.Text = "正在解码音频…";
        var progress = new Progress<double>(fraction =>
        {
            if (_analysis != request || request.IsCancellationRequested || _closed)
                return;
            double percent = Math.Clamp(fraction * 100, 0, 100);
            AnalysisProgress.Value = percent;
            StatusText.Text = $"正在识别琴音… {percent:0}%";
        });

        AudioInput? decoded = null;
        try
        {
            var loaded = await Task.Run(() =>
            {
                var audio = AudioInput.Decode(path, request.Token);
                try
                {
                    var notes = Transcriber.Recognize(audio, progress, request.Token);
                    if (notes.Count == 0)
                        throw new InvalidDataException("没有识别到清晰音符。可以试试无伴唱、背景噪声较低的钢琴独奏。");
                    return (Audio: audio, Notes: notes, Estimate: NumberScore.EstimatePulse(notes));
                }
                catch
                {
                    audio.Dispose();
                    throw;
                }
            }, request.Token);
            decoded = loaded.Audio;
            request.Token.ThrowIfCancellationRequested();
            var score = NumberScore.Create(loaded.Notes, decoded.Duration,
                new ScoreSettings(0, loaded.Estimate.Bpm, 4, loaded.Estimate.Downbeat));
            _estimate = loaded.Estimate;
            _title = Path.GetFileNameWithoutExtension(path);
            _notes = loaded.Notes;
            SetPulseFields();
            LoadTrack(decoded, score);
            decoded = null;
            SongTitle.Text = _title;
            StatusText.Text = $"识别到 {_notes.Count} 个音符 · {_score!.Measures.Count} 个小节 · 速度可手动调整";
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
                StatusText.Text = "已取消识别。";
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                StatusText.Text = "未能完成转换。";
                MessageBox.Show(this, ex.Message, "无法转换音频", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            decoded?.Dispose();
            _analysis = null;
            if (!_closed)
                SetBusy(false);
        }
    }

    private void LoadTrack(AudioInput audio, NumberScore score)
    {
        ReleaseMedia();
        _audio = audio;
        TonicBox.SelectedIndex = 0;
        MeterBox.SelectedIndex = 2;
        _updatingSlider = true;
        PositionSlider.Maximum = audio.Duration;
        _updatingSlider = false;
        TotalTime.Text = FormatTime(audio.Duration);
        SetScore(score);
        SetPosition(0);

        var player = new PcmPlayback(audio) { Speed = Speed() };
        player.Completed += error => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(player, _player) || _closed)
                return;
            UpdatePausedState();
            if (error is null)
                SetPosition(audio.Duration);
            else
                ReportPlaybackError(error.Message);
        });
        _player = player;
    }

    private void SetScore(NumberScore score)
    {
        _score = score;
        EmptyPanel.Visibility = Visibility.Collapsed;
        ReflowScore(newScore: true);
    }

    /// <summary>当谱面宽度跨过排版阈值时重新分行。</summary>
    private void ScoreSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            ReflowScore(newScore: false);
    }

    /// <summary>缩放音符和行高，并按新的可用宽度重新分行。</summary>
    private void ScoreZoomChanged(object sender, SelectionChangedEventArgs e) => ReflowScore(newScore: false);

    /// <summary>记录列表滚动容器。列表以谱行为滚动单位。</summary>
    private void ScoreScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer viewer)
            _scoreScroller = viewer;
    }

    /// <summary>重排小节后，自动模式定位到播放位置，手动模式保留原先的可见小节。</summary>
    private void ReflowScore(bool newScore)
    {
        if (_score is not { } score)
            return;
        double scale = ScoreZoomBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : .8;
        double width = ScoreList.ActualWidth - SystemParameters.VerticalScrollBarWidth;
        int columns = ScoreLineView.ColumnsForWidth(width / scale, score.MeasureTicks);
        if (!newScore && columns == _measuresPerLine && scale == _scoreScale)
            return;

        int visibleMeasure = newScore ? 0 : (int)(_scoreScroller?.VerticalOffset ?? 0) * _measuresPerLine;
        _measuresPerLine = columns;
        _scoreScale = scale;
        ScoreList.ItemsSource = score.Measures.Chunk(columns).Select(m => new ScoreLine(score, m, columns, scale)).ToArray();
        _scrolledLine = -1;
        if (FollowCheck.IsChecked == true)
            ScrollToPosition();
        else
            _scoreScroller?.ScrollToVerticalOffset(visibleMeasure / columns);
    }

    private void SetBusy(bool busy)
    {
        OpenButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AnalysisProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = !busy && !_exporting && _score is not null;
        ScoreSettingsPanel.IsEnabled = !busy && _score is not null;
        TransportPanel.IsEnabled = !busy && _player is not null;
    }

    private void CancelAnalysis(object sender, RoutedEventArgs e)
    {
        _analysis?.Cancel();
        StatusText.Text = "正在取消…";
    }

    private void ApplySettings(object sender, RoutedEventArgs e)
    {
        if (_audio is null || _analysis is not null)
            return;
        if (!ReadNumber(BpmBox.Text, out double bpm) || !ReadNumber(OffsetBox.Text, out double offset)
            || bpm is < 40 or > 220 || Math.Abs(offset) > 10)
        {
            StatusText.Text = "请输入 40–220 的 BPM，以及 -10–10 秒的首拍偏移。";
            return;
        }
        SetScore(NumberScore.Create(_notes, _audio.Duration,
            new ScoreSettings(TonicBox.SelectedIndex, bpm, MeterBox.SelectedIndex + 2, offset)));
        StatusText.Text = $"已整理为 1={NumberScore.KeyNames[TonicBox.SelectedIndex]}，{MeterBox.SelectedIndex + 2}/4，{bpm:0.##} BPM。";
    }

    private void RestorePulse(object sender, RoutedEventArgs e)
    {
        SetPulseFields();
        ApplySettings(sender, e);
    }

    private void SetPulseFields()
    {
        BpmBox.Text = _estimate.Bpm.ToString("0.##", CultureInfo.CurrentCulture);
        OffsetBox.Text = _estimate.Offset.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private static bool ReadNumber(string text, out double value)
        => (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value);

    /// <summary>将当前谱面快照导出为 PDF 或 UTF-8 文本。</summary>
    private async void ExportScore(object sender, RoutedEventArgs e)
    {
        if (_score is not { } score || _exporting)
            return;
        string title = _title;
        var dialog = new SaveFileDialog
        {
            Title = "保存简谱",
            Filter = "PDF 简谱 (*.pdf)|*.pdf|UTF-8 简谱文本 (*.txt)|*.txt",
            AddExtension = true,
            FileName = $"{title.Replace('.', '．')} - 简谱"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _exporting = true;
        ExportButton.IsEnabled = false;
        try
        {
            string path = dialog.FileName;
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".pdf":
                    StatusText.Text = "正在导出 PDF 简谱…";
                    await ScorePdf.SaveAsync(path, score, title);
                    break;
                case ".txt":
                    await File.WriteAllTextAsync(path, score.ToText(title), new UTF8Encoding(true));
                    break;
                default:
                    throw new InvalidDataException("请选择 .pdf 或 .txt 文件。");
            }
            if (!_closed && _analysis is null && ReferenceEquals(score, _score))
                StatusText.Text = $"已导出 {Path.GetFileName(path)}。";
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                if (_analysis is null && ReferenceEquals(score, _score))
                    StatusText.Text = "简谱导出失败。";
                MessageBox.Show(this, ex.Message, "无法保存简谱", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            _exporting = false;
            if (!_closed)
                ExportButton.IsEnabled = _analysis is null && _score is not null;
        }
    }

    private void TogglePlay(object sender, RoutedEventArgs e)
    {
        if (_player is not { } player || _audio is null || _analysis is not null)
            return;
        TryPlayback(() =>
        {
            if (_playing)
            {
                Pause();
                return;
            }
            if (Position >= _audio.Duration - 0.04)
                player.Seek(0);
            player.Speed = Speed();
            player.Play();
            _playing = true;
            PlayButton.Content = "Ⅱ 暂停";
            _clock.Start();
        });
    }

    private void Pause()
    {
        _player?.Pause();
        if (_player is not null && !_dragging)
            SetPosition(_player.Position);
        UpdatePausedState();
    }

    private void UpdatePausedState()
    {
        _playing = false;
        _clock.Stop();
        PlayButton.Content = "▶ 播放";
    }

    private void ChangeSpeed(object sender, SelectionChangedEventArgs e)
    {
        if (_player is { } player)
            TryPlayback(() => player.Speed = Speed());
    }

    private double Speed() => SpeedBox?.SelectedItem is ComboBoxItem { Tag: string tag }
        && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 1;

    /// <summary>以声卡已经播放的采样数更新光标，缓冲或降速不会累积计时误差。</summary>
    private void RefreshClock(object? sender, EventArgs e)
    {
        if (!_dragging && _player is { } player)
            TryPlayback(() => SetPosition(player.Position));
    }

    private void TryPlayback(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is NAudio.MmException or IOException or InvalidOperationException)
        {
            ReportPlaybackError(error.Message);
        }
    }

    /// <summary>输出设备失败时保留简谱及导出入口。</summary>
    private void ReportPlaybackError(string message)
    {
        UpdatePausedState();
        StatusText.Text = $"已生成简谱。音频播放失败：{message}";
    }

    private void StartScrub(object sender, DragStartedEventArgs e) => _dragging = true;

    private void EndScrub(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        Seek(PositionSlider.Value);
    }

    private void SlideChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || _player is null || _analysis is not null)
            return;
        if (_dragging)
            SetPosition(e.NewValue);
        else
            Seek(e.NewValue);
    }

    private void SeekScore(object sender, ScoreSeekEventArgs e) => Seek(e.Seconds);

    private void Seek(double value)
    {
        if (_player is not { } player || _audio is null || _analysis is not null)
            return;
        TryPlayback(() =>
        {
            double position = Math.Clamp(value, 0, _audio.Duration);
            player.Seek(position);
            SetPosition(position);
            if (position >= _audio.Duration)
                UpdatePausedState();
        });
    }

    private void SetPosition(double value)
    {
        if (_audio is null)
            return;
        Position = Math.Clamp(value, 0, _audio.Duration);
        CurrentTime.Text = FormatTime(Position);
        if (!_dragging)
        {
            _updatingSlider = true;
            PositionSlider.Value = Position;
            _updatingSlider = false;
        }
        ScrollToPosition();
    }

    private void FollowChanged(object sender, RoutedEventArgs e)
    {
        _scrolledLine = -1;
        ScrollToPosition();
    }

    private void ScrollToPosition()
    {
        if (_score is null || FollowCheck.IsChecked != true)
            return;
        int index = _score.MeasureAt(Position) / _measuresPerLine;
        if (index == _scrolledLine || index >= ScoreList.Items.Count)
            return;
        _scrolledLine = index;
        ScoreList.ScrollIntoView(ScoreList.Items[index]);
    }

    private void HandleShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenAudio(sender, e);
            e.Handled = true;
        }
        else if (e.OriginalSource is not (TextBox or ComboBox or ComboBoxItem or Slider or Button) && _player is not null)
        {
            switch (e.Key)
            {
                case Key.Space:
                    TogglePlay(sender, e);
                    e.Handled = true;
                    break;
                case Key.Left:
                case Key.Right:
                    Seek(Position + (e.Key == Key.Left ? -5 : 5));
                    e.Handled = true;
                    break;
            }
        }
    }

    private static string FormatTime(double value)
        => TimeSpan.FromSeconds(Math.Max(0, value)).ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private void ReleaseMedia()
    {
        UpdatePausedState();
        _player?.Dispose();
        _player = null;
        _audio?.Dispose();
        _audio = null;
    }

    private void CloseWindow(object? sender, EventArgs e)
    {
        _closed = true;
        _analysis?.Cancel();
        ReleaseMedia();
    }
}
