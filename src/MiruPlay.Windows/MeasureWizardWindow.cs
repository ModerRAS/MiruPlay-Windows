using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows;

public sealed class MeasureWizardWindow : Window, IDisposable
{
    private readonly RoomMeasureRunner _runner = new();
    private readonly AudioDspConfig _config;
    private readonly Func<AudioDspConfig, Task> _apply;
    private readonly StackPanel _body = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ComboBox _mics = new() { MinWidth = 320 };
    private readonly TextBox _serial = new() { Width = 140, MaxLength = 8 };
    private readonly ComboBox _incidence = new() { Width = 90 };
    private readonly TextBox _calibrationPath = new() { MinWidth = 280, IsReadOnly = true };
    private string? _calibrationText;
    private string? _calibrationLabel;
    private RoomMeasurePipeline.Measurement? _measurement;
    private CancellationTokenSource? _noiseCts;
    private CancellationTokenSource? _measureCts;
    private int _step;

    public MeasureWizardWindow(AudioDspConfig config, Func<AudioDspConfig, Task> apply)
    {
        _config = config;
        _apply = apply;
        Title = "房间校准向导";
        Width = 640;
        Height = 560;
        MinWidth = 520;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(_body);
        root.Children.Add(_status);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        _step = step;
        StopNoise();
        _body.Children.Clear();
        switch (step)
        {
            case 0: BuildDeviceStep(); break;
            case 1: BuildVolumeStep(); break;
            case 2: BuildMeasureStep(); break;
            default: BuildResultStep(); break;
        }
    }

    private void BuildDeviceStep()
    {
        _body.Children.Add(new TextBlock { Text = "步骤 1/4：选择麦克风", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var capabilities = RoomMeasureRunner.Probe();
        _mics.ItemsSource = capabilities.Mics;
        _mics.DisplayMemberPath = nameof(WaveAudio.AudioDevice.Name);
        _mics.SelectedItem = capabilities.Mics.FirstOrDefault(mic =>
            mic.Name.Contains("UMIK", StringComparison.OrdinalIgnoreCase));
        if (_mics.SelectedItem is null && capabilities.Mics.Count > 0) _mics.SelectedIndex = 0;
        AutomationProperties.SetName(_mics, "麦克风");
        _body.Children.Add(_mics);
        _body.Children.Add(new TextBlock
        {
            Text = capabilities.Available
                ? $"输出将使用默认音频设备（{RoomMeasureRunner.DescribeDefaultOutput() ?? "未知"}）。"
                : capabilities.Reason,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
        });
        _body.Children.Add(new TextBlock { Text = "UMIK-1 校准（可选）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        var serialRow = new StackPanel { Orientation = Orientation.Horizontal };
        serialRow.Children.Add(new TextBlock { Text = "序列号", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        AutomationProperties.SetName(_serial, "UMIK 序列号");
        serialRow.Children.Add(_serial);
        _incidence.ItemsSource = new[] { "0°", "90°" };
        _incidence.SelectedIndex = 0;
        AutomationProperties.SetName(_incidence, "校准角度");
        serialRow.Children.Add(_incidence);
        var download = new Button { Content = "下载校准文件", Margin = new Thickness(8, 0, 0, 0) };
        download.Click += async (_, _) => await DownloadCalibrationAsync();
        serialRow.Children.Add(download);
        _body.Children.Add(serialRow);
        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(_calibrationPath, "校准文件路径");
        fileRow.Children.Add(_calibrationPath);
        var browse = new Button { Content = "选择校准文件…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "校准文件 (*.txt;*.cal)|*.txt;*.cal|所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var text = File.ReadAllText(dialog.FileName);
                var parsed = MicCalibration.Parse(text);
                _calibrationText = text;
                _calibrationLabel = $"{Path.GetFileName(dialog.FileName)}（{parsed.Calibration.FrequenciesHz.Length} 点）";
                _calibrationPath.Text = _calibrationLabel;
                _status.Text = $"已载入校准：{_calibrationLabel}";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _status.Text = error.Message;
            }
        };
        fileRow.Children.Add(browse);
        _body.Children.Add(fileRow);
        var next = new Button { Content = "下一步：音量", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        next.Click += (_, _) =>
        {
            if (_mics.SelectedItem is null)
            {
                _status.Text = "请先选择麦克风。";
                return;
            }
            ShowStep(1);
        };
        _body.Children.Add(next);
    }

    private async Task DownloadCalibrationAsync()
    {
        try
        {
            _status.Text = "正在下载校准文件…";
            var text = await _runner.DownloadUmikCalibrationAsync(
                _serial.Text,
                _incidence.SelectedIndex == 1 ? MicCalibration.Incidence.NinetyDeg : MicCalibration.Incidence.ZeroDeg);
            _calibrationText = text;
            _calibrationLabel = $"UMIK-{_serial.Text.Trim()}（{(_incidence.SelectedItem as string) ?? "0°"}）";
            _calibrationPath.Text = _calibrationLabel;
            _status.Text = $"已下载校准：{_calibrationLabel}";
        }
        catch (Exception error) when (error is RoomMeasureRunner.MeasureException or HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            _status.Text = error.Message;
        }
    }

    private void BuildVolumeStep()
    {
        _body.Children.Add(new TextBlock { Text = "步骤 2/4：音量匹配", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        _body.Children.Add(new TextBlock
        {
            Text = "播放粉红噪声，把功放/系统音量调到平时听音的响度，然后继续。噪声从默认输出播放，不经过 DSP。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var play = new Button { Content = "播放噪声", Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(play, "播放粉红噪声");
        play.Click += (_, _) =>
        {
            StopNoise();
            _noiseCts = new CancellationTokenSource();
            var token = _noiseCts.Token;
            var noise = RoomMeasureRunner.BuildPinkNoisePreview(RoomMeasureRunner.SampleRateHz, 3.0);
            Task.Run(() =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                        RoomMeasureRunner.PlayPreview(noise, token);
                }
                catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or TimeoutException)
                {
                    Dispatcher.Invoke(() => _status.Text = error.Message);
                }
            }, token);
            _status.Text = "正在播放粉红噪声…";
        };
        var stop = new Button { Content = "停止", Margin = new Thickness(0, 0, 8, 0) };
        stop.Click += (_, _) => { StopNoise(); _status.Text = "已停止。"; };
        var next = new Button { Content = "下一步：测量" };
        next.Click += (_, _) => ShowStep(2);
        var back = new Button { Content = "上一步", Margin = new Thickness(0, 0, 8, 0) };
        back.Click += (_, _) => ShowStep(0);
        row.Children.Add(back);
        row.Children.Add(play);
        row.Children.Add(stop);
        row.Children.Add(next);
        _body.Children.Add(row);
    }

    private void BuildMeasureStep()
    {
        _body.Children.Add(new TextBlock { Text = "步骤 3/4：双扫频测量", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        _body.Children.Add(new TextBlock
        {
            Text = "保持安静。程序播放两次扫频并同时录音（约 12 秒），随后分析。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var progress = new TextBlock { Text = "准备…", TextWrapping = TextWrapping.Wrap };
        _body.Children.Add(progress);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消" };
        row.Children.Add(cancel);
        _body.Children.Add(row);
        _measureCts = new CancellationTokenSource();
        var token = _measureCts.Token;
        cancel.Click += (_, _) => _measureCts?.Cancel();
        var micId = (_mics.SelectedItem as WaveAudio.AudioDevice)?.Id;
        Task.Run(async () =>
        {
            try
            {
                var outcome = await RoomMeasureRunner.MeasureRoomAsync(
                    _calibrationText, micId,
                    message => Dispatcher.Invoke(() => progress.Text = message), token);
                await Dispatcher.InvokeAsync(() =>
                {
                    _measurement = outcome.Measurement;
                    ShowStep(3);
                });
            }
            catch (Exception error) when (error is RoomMeasureRunner.MeasureException or InvalidOperationException or TimeoutException or OperationCanceledException or IOException)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    progress.Text = token.IsCancellationRequested ? "已取消。" : error.Message;
                    var back = new Button { Content = "返回", Margin = new Thickness(0, 12, 0, 0) };
                    back.Click += (_, _) => ShowStep(0);
                    _body.Children.Add(back);
                });
            }
        }, token);
    }

    private void BuildResultStep()
    {
        _body.Children.Add(new TextBlock { Text = "步骤 4/4：结果", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var measurement = _measurement;
        if (measurement is null)
        {
            _body.Children.Add(new TextBlock { Text = "没有测量结果。", TextWrapping = TextWrapping.Wrap });
            return;
        }
        var summary = measurement.Valid
            ? $"有效测量：漂移 {measurement.Drift.EstimatedPpm:0.00} ppm（双扫差 {measurement.Drift.PpmSpread:0.00}），拟合 {measurement.Fit.Bands.Count} 段，残余 RMS {measurement.Fit.RmsDeviationDb:0.00} dB。"
            : $"测量被拒绝：{measurement.InvalidReason}（拟合结果不会被应用）";
        _body.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var apply = new Button
        {
            Content = "应用为 DSP 预设",
            IsEnabled = measurement.Valid,
            Margin = new Thickness(0, 0, 8, 0),
        };
        AutomationProperties.SetName(apply, "应用测量为 DSP 预设");
        apply.Click += async (_, _) =>
        {
            try
            {
                var updated = RoomMeasureAutoEq.ApplyToConfig(
                    _config,
                    new RoomMeasureAutoEq.MeasuredBands(measurement.Fit.Bands, true),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await _apply(updated);
                _status.Text = $"已应用预设并选中：{updated.SelectedPresetId}";
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or TimeoutException)
            {
                _status.Text = error.Message;
            }
        };
        var import = new Button { Content = "导入 WAV 分析…", Margin = new Thickness(0, 0, 8, 0) };
        import.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "WAV 文件 (*.wav)|*.wav" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                _status.Text = "正在分析 WAV…";
                var outcome = await RoomMeasureRunner.ImportWavAsync(
                    await File.ReadAllBytesAsync(dialog.FileName), _calibrationText,
                    message => Dispatcher.Invoke(() => _status.Text = message));
                _measurement = outcome.Measurement;
                ShowStep(3);
            }
            catch (Exception error) when (error is RoomMeasureRunner.MeasureException or InvalidDataException or IOException or OperationCanceledException)
            {
                _status.Text = error.Message;
            }
        };
        var close = new Button { Content = "关闭" };
        close.Click += (_, _) => Close();
        row.Children.Add(apply);
        row.Children.Add(import);
        row.Children.Add(close);
        _body.Children.Add(row);
    }

    private void StopNoise()
    {
        try { _noiseCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _noiseCts?.Dispose();
        _noiseCts = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        StopNoise();
        try { _measureCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _measureCts?.Dispose();
        _measureCts = null;
        GC.SuppressFinalize(this);
    }
}
