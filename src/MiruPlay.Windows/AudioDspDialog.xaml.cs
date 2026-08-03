using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows;

public partial class AudioDspDialog : Window
{
    public static IReadOnlyList<AudioDspFilterType> FilterTypes { get; } = Enum.GetValues<AudioDspFilterType>();

    private AudioDspConfig _config;
    private bool _loading;

    public AudioDspDialog(AudioDspConfig config)
    {
        InitializeComponent();
        _config = (config ?? AudioDspConfig.Neutral()).Normalize();
        _loading = true;
        EnabledBox.IsChecked = _config.Enabled;
        PresetBox.ItemsSource = _config.Presets;
        PresetBox.SelectedItem = _config.Presets!.First(item =>
            item.Id.Equals(_config.SelectedPresetId, StringComparison.OrdinalIgnoreCase));
        PhaseBox.ItemsSource = Enum.GetValues<AudioDspPhaseMode>();
        FirQualityBox.ItemsSource = Enum.GetValues<AudioDspFirQuality>();
        TargetBox.ItemsSource = Enum.GetValues<AudioDspChannelTarget>();
        OutputBox.ItemsSource = Enum.GetValues<AudioDspOutputMode>();
        LayoutBox.ItemsSource = new[] { "mono", "stereo", "5.1", "7.1" };
        _loading = false;
        LoadPresetControls();
    }

    public AudioDspConfig? Result { get; private set; }

    private AudioDspPreset SelectedPreset => (AudioDspPreset)PresetBox.SelectedItem;

    private void Preset_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_loading) LoadPresetControls();
    }

    private void LoadPresetControls()
    {
        if (PresetBox.SelectedItem is not AudioDspPreset preset) return;
        _loading = true;
        PhaseBox.SelectedItem = preset.PhaseMode;
        FirQualityBox.SelectedItem = preset.FirQuality;
        OutputBox.SelectedItem = preset.OutputMode;
        LayoutBox.SelectedItem = preset.ChannelLayoutId;
        var limiter = preset.Limiter ?? new AudioDspLimiter();
        LimiterBox.IsChecked = limiter.Enabled;
        LimiterCeilingBox.Text = limiter.CeilingDb.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        LimiterReleaseBox.Text = limiter.ReleaseMs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        TargetBox.SelectedItem = AudioDspChannelTarget.Left;
        _loading = false;
        LoadRows();
    }

    private void LoadRows()
    {
        if (PresetBox.SelectedItem is not AudioDspPreset preset || TargetBox.SelectedItem is not AudioDspChannelTarget target) return;
        var rule = (preset.Rules ?? []).FirstOrDefault(item => item.Target == target);
        BandGrid.ItemsSource = new ObservableCollection<AudioDspBandRow>(
            (rule?.Bands ?? []).Select(band => new AudioDspBandRow(band)));
    }

    private void Target_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => LoadRows();

    private async void ImportRew_Click(object sender, RoutedEventArgs e)
    {
        if (TargetBox.SelectedItem is not AudioDspChannelTarget target) return;
        var dialog = new OpenFileDialog
        {
            Title = "导入 REW 校准文件",
            Filter = "REW 文本文件 (*.txt;*.req)|*.txt;*.req|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var parsed = RewEqFileParser.Parse(await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8));
            if (parsed.Bands.Count == 0)
            {
                StatusBox.Text = parsed.Errors.Count == 0 ? "REW 文件没有可导入的启用频段。" : string.Join("; ", parsed.Errors.Select(error => $"第 {error.LineNumber} 行: {error.Message}"));
                return;
            }
            _config = AudioDspEditorState.ReplaceChannelBands(
                CaptureConfigWithoutRows(), SelectedPreset.Id, target, parsed.Bands.Select(item => item.Band).ToArray());
            LoadRows();
            StatusBox.Text = $"已导入 {parsed.Bands.Count} 个频段到 {target}。" +
                (parsed.Errors.Count > 0 ? $" 另有 {parsed.Errors.Count} 个错误。" : string.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusBox.Text = error.Message;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Result = CaptureConfigWithoutRows().Normalize();
            var errors = Result.Validate();
            if (errors.Count > 0)
            {
                StatusBox.Text = string.Join("; ", errors);
                Result = null;
                return;
            }
            DialogResult = true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            StatusBox.Text = error.Message;
        }
    }

    private AudioDspConfig CaptureConfigWithoutRows()
    {
        if (PresetBox.SelectedItem is not AudioDspPreset preset || TargetBox.SelectedItem is not AudioDspChannelTarget target)
            throw new InvalidOperationException("DSP 预设或目标声道未选择。");
        BandGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        BandGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        var rows = BandGrid.Items.OfType<AudioDspBandRow>().Select(row => row.ToBand()).ToArray();
        if (!double.TryParse(LimiterCeilingBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ceilingDb) ||
            !double.TryParse(LimiterReleaseBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var releaseMs))
            throw new ArgumentException("限幅参数必须是数字。");
        var withControls = preset with
        {
            PhaseMode = (AudioDspPhaseMode)PhaseBox.SelectedItem,
            FirQuality = (AudioDspFirQuality)FirQualityBox.SelectedItem,
            OutputMode = (AudioDspOutputMode)OutputBox.SelectedItem,
            ChannelLayoutId = (string)LayoutBox.SelectedItem,
            Limiter = new AudioDspLimiter(LimiterBox.IsChecked == true, ceilingDb, releaseMs),
        };
        var baseConfig = _config with
        {
            Enabled = EnabledBox.IsChecked == true,
            SelectedPresetId = preset.Id,
            Presets = (_config.Presets ?? []).Select(item => item.Id.Equals(preset.Id, StringComparison.OrdinalIgnoreCase) ? withControls : item).ToArray(),
        };
        return AudioDspEditorState.ReplaceChannelBands(baseConfig, preset.Id, target, rows);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    public sealed class AudioDspBandRow
    {
        public AudioDspBandRow(AudioDspBand band, int lineNumber = 0)
        {
            Enabled = band.Enabled;
            Type = band.Type;
            FrequencyHz = band.FrequencyHz;
            GainDb = band.GainDb;
            Q = band.Q;
            LineNumber = lineNumber;
        }

        public bool Enabled { get; set; }
        public AudioDspFilterType Type { get; set; }
        public double FrequencyHz { get; set; }
        public double GainDb { get; set; }
        public double Q { get; set; }
        public int LineNumber { get; }

        public AudioDspBand ToBand() => new(Type, FrequencyHz, GainDb, Q, Enabled);
    }
}
