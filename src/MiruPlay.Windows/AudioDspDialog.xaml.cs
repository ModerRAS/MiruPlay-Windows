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
    private readonly ToppingController? _topping;
    private readonly Func<AudioDspConfig, Task>? _applyMeasured;
    private bool _loading;

    public AudioDspDialog(AudioDspConfig config, ToppingController? topping = null, Func<AudioDspConfig, Task>? applyMeasured = null)
    {
        InitializeComponent();
        _topping = topping;
        _applyMeasured = applyMeasured;
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
            Title = AppStrings.Get("wpf.dsp.importRew"),
            Filter = "REW " + AppStrings.Get("wpf.dsp.rewTextFile") + " (*.txt;*.req)|*.txt;*.req|" + AppStrings.Get("wpf.common.allFiles") + " (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var parsed = RewEqFileParser.Parse(await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8));
            if (parsed.Bands.Count == 0)
            {
                StatusBox.Text = parsed.Errors.Count == 0 ? AppStrings.Get("wpf.dsp.noBands") : string.Join("; ", parsed.Errors.Select(error => AppStrings.Get("wpf.dsp.lineError", error.LineNumber, error.Message)));
                return;
            }
            _config = AudioDspEditorState.ReplaceChannelBands(
                CaptureConfigWithoutRows(), SelectedPreset.Id, target, parsed.Bands.Select(item => item.Band).ToArray());
            LoadRows();
            StatusBox.Text = AppStrings.Get("wpf.dsp.importedBands", parsed.Bands.Count, target) +
                (parsed.Errors.Count > 0 ? AppStrings.Get("wpf.dsp.moreErrors", parsed.Errors.Count) : string.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusBox.Text = error.Message;
        }
    }

    private async void PushToTopping_Click(object sender, RoutedEventArgs e)
    {
        if (_topping is null)
        {
            StatusBox.Text = AppStrings.Get("api.errNoTopping");
            return;
        }
        AudioDspPreset preset;
        try
        {
            preset = CaptureConfigWithoutRows().Presets!.First(item =>
                item.Id.Equals(SelectedPreset.Id, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            StatusBox.Text = error.Message;
            return;
        }
        try
        {
            var mapping = await _topping.ApplyPresetAsync(preset);
            StatusBox.Text = mapping.Warnings.Count == 0
                ? AppStrings.Get("wpf.dsp.pushedBands", mapping.Bands.Count(band => band.On))
                : AppStrings.Get("wpf.dsp.pushedWarns", mapping.Warnings.Count, string.Join("; ", mapping.Warnings));
        }
        catch (ToppingController.ToppingCommandException error)
        {
            StatusBox.Text = error.Message;
        }
    }

    private void MeasureWizard_Click(object sender, RoutedEventArgs e)
    {
        if (_applyMeasured is null)
        {
            StatusBox.Text = AppStrings.Get("wpf.dsp.wizardUnavailable");
            return;
        }
        var wizard = new MeasureWizardWindow(CaptureConfigWithoutRows().Normalize(), _applyMeasured)
        {
            Owner = this,
        };
        wizard.ShowDialog();
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
            throw new InvalidOperationException(AppStrings.Get("wpf.dsp.noPreset"));
        BandGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        BandGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        var rows = BandGrid.Items.OfType<AudioDspBandRow>().Select(row => row.ToBand()).ToArray();
        if (!double.TryParse(LimiterCeilingBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ceilingDb) ||
            !double.TryParse(LimiterReleaseBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var releaseMs))
            throw new ArgumentException(AppStrings.Get("wpf.dsp.limiterDigits"));
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
