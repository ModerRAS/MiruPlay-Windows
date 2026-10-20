using System.Globalization;
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
        Title = AppStrings.Get("wpf.measureWizard");
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
        _body.Children.Add(new TextBlock { Text = AppStrings.Get("wpf.measure.step1"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var capabilities = RoomMeasureRunner.Probe();
        _mics.ItemsSource = capabilities.Mics;
        _mics.DisplayMemberPath = nameof(WaveAudio.AudioDevice.Name);
        _mics.SelectedItem = capabilities.Mics.FirstOrDefault(mic =>
            mic.Name.Contains("UMIK", StringComparison.OrdinalIgnoreCase));
        if (_mics.SelectedItem is null && capabilities.Mics.Count > 0) _mics.SelectedIndex = 0;
        AutomationProperties.SetName(_mics, AppStrings.Get("wpf.measure.mic"));
        _body.Children.Add(_mics);
        _body.Children.Add(new TextBlock
        {
            Text = capabilities.Available
                ? AppStrings.Get("wpf.measure.outputNote", RoomMeasureRunner.DescribeDefaultOutput() ?? AppStrings.Get("common.unknown"))
                : capabilities.Reason,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
        });
        _body.Children.Add(new TextBlock { Text = AppStrings.Get("wpf.measure.umikTitle"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        var serialRow = new StackPanel { Orientation = Orientation.Horizontal };
        serialRow.Children.Add(new TextBlock { Text = AppStrings.Get("wpf.measure.serial"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        AutomationProperties.SetName(_serial, AppStrings.Get("wpf.measure.serialName"));
        serialRow.Children.Add(_serial);
        _incidence.ItemsSource = new[] { "0°", "90°" };
        _incidence.SelectedIndex = 0;
        AutomationProperties.SetName(_incidence, AppStrings.Get("wpf.measure.incidence"));
        serialRow.Children.Add(_incidence);
        var download = new Button { Content = AppStrings.Get("wpf.measure.download"), Margin = new Thickness(8, 0, 0, 0) };
        download.Click += async (_, _) => await DownloadCalibrationAsync();
        serialRow.Children.Add(download);
        _body.Children.Add(serialRow);
        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(_calibrationPath, AppStrings.Get("wpf.measure.calPath"));
        fileRow.Children.Add(_calibrationPath);
        var browse = new Button { Content = AppStrings.Get("wpf.measure.browseFile"), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = AppStrings.Get("wpf.measure.calFilter") + " (*.txt;*.cal)|*.txt;*.cal|" + AppStrings.Get("wpf.common.allFiles") + " (*.*)|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var text = File.ReadAllText(dialog.FileName);
                var parsed = MicCalibration.Parse(text);
                _calibrationText = text;
                _calibrationLabel = AppStrings.Get("wpf.measure.calPoints", Path.GetFileName(dialog.FileName), parsed.Calibration.FrequenciesHz.Length);
                _calibrationPath.Text = _calibrationLabel;
                _status.Text = AppStrings.Get("wpf.measure.loadedCalibration", _calibrationLabel);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _status.Text = error.Message;
            }
        };
        fileRow.Children.Add(browse);
        _body.Children.Add(fileRow);
        var next = new Button { Content = AppStrings.Get("wpf.measure.nextVolume"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        next.Click += (_, _) =>
        {
            if (_mics.SelectedItem is null)
            {
                _status.Text = AppStrings.Get("wpf.measure.selectMicFirst");
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
            _status.Text = AppStrings.Get("wpf.measure.downloading");
            var text = await _runner.DownloadUmikCalibrationAsync(
                _serial.Text,
                _incidence.SelectedIndex == 1 ? MicCalibration.Incidence.NinetyDeg : MicCalibration.Incidence.ZeroDeg);
            _calibrationText = text;
            _calibrationLabel = $"UMIK-{_serial.Text.Trim()}（{(_incidence.SelectedItem as string) ?? "0°"}）";
            _calibrationPath.Text = _calibrationLabel;
            _status.Text = AppStrings.Get("wpf.measure.downloadedCalibration", _calibrationLabel);
        }
        catch (Exception error) when (error is RoomMeasureRunner.MeasureException or HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            _status.Text = error.Message;
        }
    }

    private void BuildVolumeStep()
    {
        _body.Children.Add(new TextBlock { Text = AppStrings.Get("wpf.measure.step2"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        _body.Children.Add(new TextBlock
        {
            Text = AppStrings.Get("wpf.measure.volumeNote"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var play = new Button { Content = AppStrings.Get("wpf.measure.playNoise"), Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(play, AppStrings.Get("wpf.measure.playPink"));
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
            _status.Text = AppStrings.Get("wpf.measure.noisePlaying");
        };
        var stop = new Button { Content = AppStrings.Get("wpf.measure.stop"), Margin = new Thickness(0, 0, 8, 0) };
        stop.Click += (_, _) => { StopNoise(); _status.Text = AppStrings.Get("wpf.measure.stopped"); };
        var next = new Button { Content = AppStrings.Get("wpf.measure.nextMeasure") };
        next.Click += (_, _) => ShowStep(2);
        var back = new Button { Content = AppStrings.Get("wpf.measure.back"), Margin = new Thickness(0, 0, 8, 0) };
        back.Click += (_, _) => ShowStep(0);
        row.Children.Add(back);
        row.Children.Add(play);
        row.Children.Add(stop);
        row.Children.Add(next);
        _body.Children.Add(row);
    }

    private void BuildMeasureStep()
    {
        _body.Children.Add(new TextBlock { Text = AppStrings.Get("wpf.measure.step3"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        _body.Children.Add(new TextBlock
        {
            Text = AppStrings.Get("wpf.measure.measureNote"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var progress = new TextBlock { Text = AppStrings.Get("wpf.measure.preparing"), TextWrapping = TextWrapping.Wrap };
        _body.Children.Add(progress);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = AppStrings.Get("wpf.cancel") };
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
                    progress.Text = token.IsCancellationRequested ? AppStrings.Get("wpf.measure.cancelled") : error.Message;
                    var back = new Button { Content = AppStrings.Get("wpf.measure.return"), Margin = new Thickness(0, 12, 0, 0) };
                    back.Click += (_, _) => ShowStep(0);
                    _body.Children.Add(back);
                });
            }
        }, token);
    }

    private void BuildResultStep()
    {
        _body.Children.Add(new TextBlock { Text = AppStrings.Get("wpf.measure.step4"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var measurement = _measurement;
        if (measurement is null)
        {
            _body.Children.Add(new TextBlock { Text = AppStrings.Get("wpf.measure.noResult"), TextWrapping = TextWrapping.Wrap });
            return;
        }
        var summary = measurement.Valid
            ? AppStrings.Get("wpf.measure.validSummary", measurement.Drift.EstimatedPpm.ToString("0.00", CultureInfo.CurrentCulture), measurement.Drift.PpmSpread.ToString("0.00", CultureInfo.CurrentCulture), measurement.Fit.Bands.Count, measurement.Fit.RmsDeviationDb.ToString("0.00", CultureInfo.CurrentCulture))
            : AppStrings.Get("wpf.measure.invalidSummary", measurement.InvalidReason);
        _body.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var apply = new Button
        {
            Content = AppStrings.Get("wpf.measure.applyPreset"),
            IsEnabled = measurement.Valid,
            Margin = new Thickness(0, 0, 8, 0),
        };
        AutomationProperties.SetName(apply, AppStrings.Get("wpf.measure.applyPresetName"));
        apply.Click += async (_, _) =>
        {
            try
            {
                var updated = RoomMeasureAutoEq.ApplyToConfig(
                    _config,
                    new RoomMeasureAutoEq.MeasuredBands(measurement.Fit.Bands, true),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await _apply(updated);
                _status.Text = AppStrings.Get("wpf.measure.presetApplied", updated.SelectedPresetId);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or TimeoutException)
            {
                _status.Text = error.Message;
            }
        };
        var import = new Button { Content = AppStrings.Get("wpf.measure.importWav"), Margin = new Thickness(0, 0, 8, 0) };
        import.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = AppStrings.Get("wpf.measure.wavFile") + " (*.wav)|*.wav" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                _status.Text = AppStrings.Get("wpf.measure.analyzingWav");
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
        var close = new Button { Content = AppStrings.Get("wpf.measure.close") };
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
