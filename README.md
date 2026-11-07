# Piano

**钢琴跟谱（Piano Follow）**是一个 C# / WPF Windows 桌面应用。它将本地钢琴音频转成可滚动的数字简谱，并以原音频的播放位置显示光标和当前音符。支持复音，同拍的多个音会分别显示。

## 使用

1. 双击 `artifacts/win-x64/PianoFollow.exe`。在其他 Windows x64 电脑上运行时，需要安装 .NET 10 Desktop Runtime，并保留发布目录中的其余文件。
2. 选择「导入钢琴曲」，可导入最长 15 分钟的 WAV、MP3、FLAC、M4A、AAC 等音频。可用格式由 Windows 音频解码器决定。
3. 点击播放开始跟谱。点击谱面可定位，也可拖动进度条。播放器支持 0.5× 至 1.25× 保持音高变速，并可关闭自动滚动。谱面会随窗口宽度、拍号和缩放自动调整每行的小节数量。默认 80%，可切换到 65% 查看更多小节，或切换到 100% 放大音符。
4. 可以修改「1 =」、拍号、BPM 和首拍偏移。调号只改记谱方式，原音频与识别出的音高保持原样。点击「恢复估计」重新应用导入时的速度和偏移。
5. 「导出简谱」可选 PDF 或 UTF-8 文本。PDF 默认以 A4 横向排版全曲并自动分页，保留调号、拍号、速度、高低音区和小节时间。

空格键播放或暂停，左右方向键跳转 5 秒，Ctrl+O 导入。输入框和进度条保留各自的键盘操作。高低音按中央 C 分区。上下点表示八度，附点增加一半时值，短横延一拍，小弧线表示同音延续。

识别、排谱和播放均在本机完成。谱面按固定速度量化到十六分音符。速度自由、混响重、伴唱或多乐器混合的录音可能出现漏音、泛音或小节错位。拍号默认为 4/4。首拍偏移可用于调整小节线，播放高亮仍读取原音频时钟。

## 构建和验证

要求 Windows x64 和 .NET 10 SDK。首次还原需要访问 NuGet。

```powershell
dotnet restore .\Piano.slnx
dotnet build .\Piano.slnx -c Release --no-restore
dotnet run --project .\tests\PianoFollow.Checks -c Release --no-build
dotnet publish .\src\PianoFollow -c Release --no-restore -o .\artifacts\win-x64
```

本地检查生成带有已知时间和音高的复音样本，验证音频解码、复音和真实八度、跨小节延音、时间定位、取消和导出。也可用检查程序的 `--make-fixture 路径.wav` 和 `--ui 路径.wav 截图.png` 运行离屏的实际播放检查。

## 第三方组件

识别使用 Spotify Basic Pitch ICASSP 2022 的官方 `nmp.onnx`，来源为 Basic Pitch v0.4.0 对应提交 `9991303bba609a3b93089d13ec80d1d495083596`。ONNX Runtime 负责模型推理，NAudio 和 Windows Media Foundation 负责音频解码与播放。[SoundTouch.Net 2.3.2](https://www.nuget.org/packages/SoundTouch.Net/2.3.2) 负责变速，随应用以独立、可替换的 DLL 发布。[SoundTouch.Net 源码](https://github.com/owoudenberg/soundtouch.net)。PDF 导出使用 [PDFsharp-WPF 6.2.4](https://www.nuget.org/packages/PDFsharp-WPF/6.2.4)。许可证和版权说明位于 `src/PianoFollow.Core/Model`，发布目录也会包含这些文件。
