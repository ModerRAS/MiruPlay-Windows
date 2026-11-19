using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MiruPlay.Windows.Services;
using MiruPlay.Windows.Models;

namespace MiruPlay.Windows.Tv;

/// <summary>Grouped TV settings list: left/right adjusts, select cycles, text fields are phone-filled.</summary>
public sealed class TvSettingsScreen : TvScreenBase
{
    private readonly TvAppContext _context;
    private readonly TvShellWindow _shell;
    private readonly StackPanel _list = new() { Margin = new Thickness(0, 0, 0, 40) };
    private readonly List<SettingRow> _rows = [];
    private readonly DispatcherTimer _statusTimer;
    private SettingRow? _gamepadStatusRow;
    private SettingRow? _updateRow;
    private TextBlock? _gamepadWarningText;
    private TvZone? _zone;

    public TvSettingsScreen(TvAppContext context, TvShellWindow shell)
    {
        _context = context;
        _shell = shell;
        TvTheme.Apply(this);
        Background = (Brush)FindResource("TvBackgroundBrush");
        var scroll = new ScrollViewer
        {
            Style = TvTheme.Style(this, "TvVerticalScrollViewerStyle"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };
        var content = new StackPanel { Margin = new Thickness(64, 30, 64, 0), MaxWidth = 1500, HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(new TextBlock { Text = AppStrings.Get("tv.settings.title"), FontSize = 34, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = AppStrings.Get("tv.settings.subtitle"),
            FontSize = 17,
            Margin = new Thickness(0, 6, 0, 18),
            Foreground = (Brush)FindResource("TvTextMutedBrush"),
        });
        content.Children.Add(_list);
        scroll.Content = content;
        Content = scroll;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => { UpdateGamepadStatus(); UpdateUpdateStatus(); };
        Build();
    }

    public override TvScreenId Id => TvScreenId.Settings;

    public override string HintText => AppStrings.Get("tv.hint.settings");

    public override bool HandleAction(TvInputAction action)
    {
        if (action is TvInputAction.Left or TvInputAction.Right && ActiveZone is { } zone && zone.Id == "settings")
        {
            var focusables = _rows.Where(row => row.Focusable).ToList();
            if (ActiveIndex >= 0 && ActiveIndex < focusables.Count)
            {
                var row = focusables[ActiveIndex];
                row.Adjust(action == TvInputAction.Right ? 1 : -1);
                row.ValueText.Text = row.Value();
                return true;
            }
        }
        return base.HandleAction(action);
    }

    public override void OnActivated(string? focusKey)
    {
        base.OnActivated(focusKey);
        UpdateGamepadStatus();
        _statusTimer.Start();
    }

    public override string? OnDeactivating()
    {
        _statusTimer.Stop();
        return base.OnDeactivating();
    }

    private static string GamepadStatusTextFor(TvGamepadStatus status) => status.Demo
        ? AppStrings.Get("tv.settings.gamepadStatusDemo")
        : status.Connected
            ? status.ActiveBackend == "SDL3"
                ? AppStrings.Get("tv.settings.gamepadStatusActiveSdl", status.DeviceName ?? "SDL3")
                : AppStrings.Get("tv.settings.gamepadStatusActiveXInput")
            : status.Sdl3InitFailed
                ? AppStrings.Get("tv.settings.gamepadStatusXInputFallback")
                : AppStrings.Get("tv.settings.gamepadStatusDisconnected");

    private string GamepadStatusText() => GamepadStatusTextFor(_shell.GamepadStatus);

    private AutoUpdateScheduler? UpdateScheduler => _shell.AutoUpdate;

    private string UpdateStatusText()
    {
        var state = UpdateScheduler?.State;
        if (state is null) return AppStrings.Get("tv.update.unavailable");
        if (string.Equals(_context.Settings.AutoUpdateMode, AutoUpdateScheduler.ModeOff, StringComparison.OrdinalIgnoreCase) &&
            state.Kind == AutoUpdateStateKind.Idle)
            return AppStrings.Get("tv.update.modeOff");
        return state.Kind switch
        {
            AutoUpdateStateKind.Checking => AppStrings.Get("tv.update.checking"),
            AutoUpdateStateKind.Downloading => AppStrings.Get("tv.update.downloading"),
            AutoUpdateStateKind.Available => AppStrings.Get("tv.update.available", state.VersionName ?? string.Empty),
            AutoUpdateStateKind.Ready => AppStrings.Get("tv.update.ready"),
            AutoUpdateStateKind.Failed => AppStrings.Get("tv.update.failed", state.Error ?? string.Empty),
            _ => AppStrings.Get("tv.update.idle"),
        };
    }

    private void UpdateUpdateStatus()
    {
        if (_updateRow is null) return;
        _updateRow.ValueText.Text = UpdateStatusText();
    }

    private void TriggerUpdateCheck()
    {
        var scheduler = UpdateScheduler;
        if (scheduler is null)
        {
            _shell.ShowToast(AppStrings.Get("tv.update.unavailable"));
            return;
        }
        if (scheduler.State.Kind == AutoUpdateStateKind.Ready)
        {
            _shell.ShowToast(AppStrings.Get("tv.update.guideDesktop"));
            return;
        }
        _ = scheduler.CheckNowAsync();
        _shell.ShowToast(AppStrings.Get("tv.update.checking"));
        UpdateUpdateStatus();
    }

    private void UpdateGamepadStatus()
    {
        if (_gamepadStatusRow is null) return;
        var status = _shell.GamepadStatus;
        _gamepadStatusRow.ValueText.Text = GamepadStatusTextFor(status);
        if (_gamepadWarningText is null) return;
        if (status.Sdl3InitFailed)
        {
            _gamepadWarningText.Text = AppStrings.Get(
                "tv.settings.gamepadWarningSdlInitFailed",
                status.Sdl3Error ?? AppStrings.Get("tv.settings.gamepadBackendValue"));
            _gamepadWarningText.Visibility = Visibility.Visible;
        }
        else
        {
            _gamepadWarningText.Visibility = Visibility.Collapsed;
        }
    }

    public override bool FocusInitial()
    {
        if (_zone is { Items.Count: > 0 })
        {
            FocusZone(_zone, 0);
            return true;
        }
        return false;
    }

    public void Refresh()
    {
        var memory = OnDeactivating();
        Build();
        if (memory is not null) RestoreFocus(memory);
    }

    private void Build()
    {
        _list.Children.Clear();
        _rows.Clear();
        if (_zone is not null) UnregisterZone(_zone);
        AddGroup("tv.settings.group.playback");
        AddRow("endAction", "tv.settings.endAction", () => ValueEndAction(), step => ChangeEndAction(step));
        AddRow("subtitle", "tv.settings.subtitleLanguage", () => ValueSubtitle(), step => ChangeSubtitle(step));
        AddRow("hdrMode", "tv.settings.hdrMode", () => ValueHdrMode(), step => ChangeHdrMode(step));
        AddRow("audioPassthrough", "tv.settings.audioPassthrough", () => ValueAudioPassthrough(), step => ChangeAudioPassthrough(step));
        AddRow("upscaleQuality", "tv.settings.upscaleQuality", () => ValueUpscale(), step => ChangeUpscale(step));
        AddRow("anime4kPreset", "tv.settings.anime4kPreset", () => ValueAnime4K(), step => ChangeAnime4K(step));
        AddRow("interpolation", "tv.settings.interpolation", () => ValueFrameInterpolation(), step => ChangeFrameInterpolation(step));
        AddRow("rifeFactor", "tv.settings.rifeFactor", () => ValueRifeFactor(), step => ChangeRifeFactor(step));
        AddRow("rifeModel", "tv.settings.rifeModel", () => ValueRifeModel(), step => ChangeRifeModel(step));
        AddRow("rifeHeight", "tv.settings.rifeHeight", () => ValueRifeHeight(), step => ChangeRifeHeight(step));
        AddRow("rifeGpuThread", "tv.settings.rifeGpuThread", () => ValueRifeGpuThread(), step => ChangeRifeGpuThread(step));
        AddGroup("tv.settings.group.tv");
        AddRow("startMode", "tv.settings.startTvMode", () => Bool(_context.Settings.TvModeEnabled), _ => Toggle(settings => settings with { TvModeEnabled = !settings.TvModeEnabled }));
        AddRow("preset", "tv.settings.remotePreset", () => ValuePreset(), step => ChangePreset());
        AddRow("exitHold", "tv.settings.exitHold", () => AppStrings.Get("tv.settings.exitHoldValue", _context.Settings.TvExitLongPressMs), step => ChangeExitHold(step));
        AddRow("gamepad", "tv.settings.gamepad", () => Bool(_context.Settings.TvGamepadEnabled), _ => Toggle(settings => settings with { TvGamepadEnabled = !settings.TvGamepadEnabled }));
        AddRow("gamepadDeadzone", "tv.settings.gamepadDeadzone", () => AppStrings.Get("tv.settings.gamepadDeadzoneValue", _context.Settings.TvGamepadDeadzonePercent), step => ChangeGamepadDeadzone(step));
        AddRow("gamepadLayout", "tv.settings.gamepadLayout", () => ValueGamepadLayout(), _ => ChangeGamepadLayout());
        _gamepadStatusRow = AddInfoRow("gamepadBackend", "tv.settings.gamepadBackend", GamepadStatusText());
        _gamepadWarningText = new TextBlock
        {
            FontSize = 15,
            Foreground = (Brush)FindResource("TvDangerBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 2, 2, 10),
            Visibility = Visibility.Collapsed,
        };
        _list.Children.Add(_gamepadWarningText);
        AddHintRow("tv.settings.gamepadHint");
        AddRow("clock", "tv.settings.clock", () => Bool(_context.Settings.TvClockVisible), _ => Toggle(settings => settings with { TvClockVisible = !settings.TvClockVisible }));
        AddRow("sound", "tv.settings.sound", () => Bool(_context.Settings.TvSoundFeedback), _ => Toggle(settings => settings with { TvSoundFeedback = !settings.TvSoundFeedback }));
        AddGroup("tv.settings.group.source");
        var source = _context.ActiveSource;
        AddInfoRow("sourceName", "tv.settings.source", source is null ? AppStrings.Get("tv.settings.sourceNone") : $"{source.Name}  ·  {source.TypeLabel}");
        AddInfoRow("sourcePath", "tv.settings.sourcePath", source?.Location ?? AppStrings.Get("tv.settings.sourceNone"));
        AddInfoRow("sourceHint", "tv.settings.sourceEdit", AppStrings.Get("tv.settings.sourceEditHint"));
        AddRow("scheduleSource", "tv.settings.scheduleSource",
            () => _context.Settings.ScheduleDataSourceUrl.Length > 0
                ? AppStrings.Get("tv.schedule.sourceConfigured")
                : AppStrings.Get("tv.schedule.sourceNotConfigured"),
            _ => _shell.ShowToast(AppStrings.Get("tv.settings.scheduleSourceHint")), actionRow: true);
        AddGroup("tv.settings.group.about");
        _updateRow = AddRow("updateCheck", "tv.settings.update", UpdateStatusText, _ => TriggerUpdateCheck(), actionRow: true);
        AddInfoRow("version", "tv.settings.version", (Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.1.0") + "  ·  " + EditionInfo.Current);
        AddInfoRow("mode", "tv.settings.appMode", AppStrings.Get(_context.ContentMode == "DRAMA" ? "tv.settings.modeDrama" : "tv.settings.modeAnime"));
        AddInfoRow("remoteDiagnostics", "tv.settings.diagnostics", AppStrings.Get("tv.settings.diagnosticsHint"));
        AddRow("exit", "tv.settings.exitTv", () => string.Empty, _ => _shell.ExitTvMode(), actionRow: true);

        var items = _rows.Where(row => row.Focusable).Select(row => (FrameworkElement)row.Element).ToList();
        _zone = new TvZone("settings", TvZoneKind.Vertical, items, onFocusChanged: index => items[index].BringIntoView());
        RegisterZone(_zone);
        if (ActiveZone is null) FocusInitial();
    }

    private void AddHintRow(string textKey)
    {
        _list.Children.Add(new TextBlock
        {
            Text = AppStrings.Get(textKey),
            FontSize = 15,
            Foreground = (Brush)FindResource("TvTextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 2, 2, 10),
        });
    }

    private void AddGroup(string titleKey)
    {
        _list.Children.Add(new TextBlock
        {
            Text = AppStrings.Get(titleKey),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TvAccentBrush"),
            Margin = new Thickness(0, 26, 0, 10),
        });
    }

    private SettingRow AddRow(string id, string labelKey, Func<string> value, Action<int> adjust, bool actionRow = false)
    {
        var valueText = new TextBlock
        {
            Text = value(),
            FontSize = 19,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (Brush)FindResource("TvTextSecondaryBrush"),
        };
        var row = new Button
        {
            Style = TvTheme.Style(this, "TvRowButton"),
            FocusVisualStyle = null,
            Height = 62,
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = BuildRowContent(labelKey, valueText),
        };
        row.Click += (_, _) =>
        {
            adjust(1);
            valueText.Text = value();
        };
        _list.Children.Add(row);
        var settingRow = new SettingRow(id, row, valueText, value, adjust, Focusable: true);
        _rows.Add(settingRow);
        return settingRow;
    }

    private SettingRow AddInfoRow(string id, string labelKey, string value)
    {
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 17,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right,
            MaxWidth = 720,
            Margin = new Thickness(40, 0, 0, 0),
            Foreground = (Brush)FindResource("TvTextMutedBrush"),
        };
        var row = new Button
        {
            Style = TvTheme.Style(this, "TvRowButton"),
            FocusVisualStyle = null,
            Height = 62,
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = BuildRowContent(labelKey, valueText),
        };
        row.Click += (_, _) => _shell.ShowToast(AppStrings.Get("tv.settings.phoneHint"));
        _list.Children.Add(row);
        var settingRow = new SettingRow(id, row, valueText, () => valueText.Text, _ => { }, Focusable: true);
        _rows.Add(settingRow);
        return settingRow;
    }

    private static Grid BuildRowContent(string labelKey, TextBlock valueText)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock
        {
            Text = AppStrings.Get(labelKey),
            FontSize = 19,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 24, 0),
        };
        grid.Children.Add(label);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        return grid;
    }

    private void Toggle(Func<AppSettings, AppSettings> update)
    {
        var settings = update(_context.Settings);
        _context.SaveSettings(settings);
        _shell.OnTvSettingsChanged();
        RefreshValues();
    }

    private void ChangeEndAction(int step)
    {
        var current = _context.Settings.PlaybackEndAction;
        var next = current == "play_next_episode" ? "return_to_detail" : "play_next_episode";
        _context.SaveSettings(_context.Settings with { PlaybackEndAction = next });
        RefreshValues();
    }

    private void ChangeSubtitle(int step)
    {
        var values = new[] { "auto", "zh_hans", "zh_hant", "en", "ja" };
        var index = Array.IndexOf(values, _context.Settings.PreferredSubtitleLanguage);
        if (index < 0) index = 0;
        index = (index + step + values.Length) % values.Length;
        _context.SaveSettings(_context.Settings with { PreferredSubtitleLanguage = values[index] });
        RefreshValues();
    }

    private void ChangeHdrMode(int step)
    {
        var values = MpvPlaybackModeSettings.HdrModeValues.ToArray();
        var index = Array.IndexOf(values, AppSettingsStore.NormalizeHdrMode(_context.Settings.HdrMode));
        if (index < 0) index = 0;
        index = (index + step + values.Length) % values.Length;
        _context.SaveSettings(_context.Settings with { HdrMode = values[index] });
        RefreshValues();
        _shell.ShowToast(AppStrings.Get("tv.settings.nextPlaybackHint"));
    }

    private string ValueHdrMode() => AppSettingsStore.NormalizeHdrMode(_context.Settings.HdrMode) switch
    {
        MpvPlaybackModeSettings.HdrModePassthrough => AppStrings.Get("tv.settings.hdr.passthrough"),
        MpvPlaybackModeSettings.HdrModeSdrToneMap => AppStrings.Get("tv.settings.hdr.sdrToneMap"),
        _ => AppStrings.Get("tv.settings.hdr.auto"),
    };

    private void ChangeAudioPassthrough(int step)
    {
        var values = new[] { "off", "auto", "force" };
        var index = Array.IndexOf(values, AppSettingsStore.NormalizeAudioPassthroughMode(_context.Settings.AudioPassthroughMode));
        if (index < 0) index = 0;
        index = (index + step + values.Length) % values.Length;
        _context.SaveSettings(_context.Settings with { AudioPassthroughMode = values[index] });
        RefreshValues();
        _shell.ShowToast(AppStrings.Get("tv.settings.nextPlaybackHint"));
    }

    private string ValueAudioPassthrough() => AppSettingsStore.NormalizeAudioPassthroughMode(_context.Settings.AudioPassthroughMode) switch
    {
        "auto" => AppStrings.Get("tv.settings.passthrough.auto"),
        "force" => AppStrings.Get("tv.settings.passthrough.force"),
        _ => AppStrings.Get("tv.settings.passthrough.off"),
    };

    private void ChangeUpscale(int step)
    {
        var values = MpvQualityPresetSettings.UpscaleValues.ToArray();
        var index = Array.IndexOf(values, AppSettingsStore.NormalizeMpvUpscalePreset(_context.Settings.MpvUpscalePreset));
        if (index < 0) index = 0;
        index = (index + step + values.Length) % values.Length;
        _context.SaveSettings(_context.Settings with { MpvUpscalePreset = values[index] });
        RefreshValues();
        _shell.ShowToast(AppStrings.Get("tv.settings.nextPlaybackHint"));
    }

    private string ValueUpscale() => AppSettingsStore.NormalizeMpvUpscalePreset(_context.Settings.MpvUpscalePreset) switch
    {
        MpvQualityPresetSettings.UpscaleLow => AppStrings.Get("tv.settings.upscale.low"),
        MpvQualityPresetSettings.UpscaleMedium => AppStrings.Get("tv.settings.upscale.medium"),
        MpvQualityPresetSettings.UpscaleHigh => AppStrings.Get("tv.settings.upscale.high"),
        _ => AppStrings.Get("tv.settings.upscale.off"),
    };

    private void ChangeAnime4K(int step)
    {
        var values = MpvQualityPresetSettings.Anime4KValues.ToArray();
        var index = Array.IndexOf(values, AppSettingsStore.NormalizeMpvAnime4KPreset(_context.Settings.MpvAnime4KPreset));
        if (index < 0) index = 0;
        index = (index + step + values.Length) % values.Length;
        _context.SaveSettings(_context.Settings with { MpvAnime4KPreset = values[index] });
        RefreshValues();
        _shell.ShowToast(AppStrings.Get("tv.settings.nextPlaybackHint"));
    }

    private string ValueAnime4K() => AppSettingsStore.NormalizeMpvAnime4KPreset(_context.Settings.MpvAnime4KPreset) switch
    {
        MpvQualityPresetSettings.Anime4KA => AppStrings.Get("tv.settings.anime4k.a"),
        MpvQualityPresetSettings.Anime4KAPlusA => AppStrings.Get("tv.settings.anime4k.aPlusA"),
        MpvQualityPresetSettings.Anime4KB => AppStrings.Get("tv.settings.anime4k.b"),
        MpvQualityPresetSettings.Anime4KC => AppStrings.Get("tv.settings.anime4k.c"),
        _ => AppStrings.Get("tv.settings.anime4k.off"),
    };

    private void ChangeFrameInterpolation(int step)
    {
        // SupportedValues now includes Rife; playback falls back with a reason when the pack is not ready.
        var values = MpvFrameInterpolationModes.SupportedValues.ToArray();
        var index = Array.IndexOf(values, AppSettingsStore.NormalizeFrameInterpolationMode(_context.Settings.FrameInterpolationMode));
        if (index < 0) index = 0;
        index = (index + step + values.Length) % values.Length;
        _context.SaveSettings(_context.Settings with { FrameInterpolationMode = values[index] });
        RefreshValues();
        var status = _context.RifeEnhancement.GetStatus();
        _shell.ShowToast(values[index] == MpvFrameInterpolationModes.Rife && !status.Ready
            ? AppStrings.Get("tv.settings.rifeUnavailable")
            : AppStrings.Get("tv.settings.nextPlaybackHint"));
    }

    private string ValueFrameInterpolation() =>
        AppSettingsStore.NormalizeFrameInterpolationMode(_context.Settings.FrameInterpolationMode) switch
        {
            MpvFrameInterpolationModes.Rife => AppStrings.Get("playback.interpolationRife"),
            MpvFrameInterpolationModes.MpvBuiltIn => AppStrings.Get("tv.settings.interpolation.mpv"),
            _ => AppStrings.Get("tv.settings.interpolation.off"),
        };

    private void ChangeRifeFactor(int step)
    {
        var values = RifeEnhancementSettings.FactorValues;
        var current = RifeEnhancementSettings.Normalize(_context.Settings.RifeEnhancement);
        var index = values.ToList().IndexOf(current.Factor);
        if (index < 0) index = 0;
        index = (index + step + values.Count) % values.Count;
        _context.SaveSettings(_context.Settings with { RifeEnhancement = current with { Factor = values[index] } });
        RefreshValues();
        _shell.ShowToast(AppStrings.Get("tv.settings.nextPlaybackHint"));
    }

    private string ValueRifeFactor() =>
        RifeEnhancementSettings.Normalize(_context.Settings.RifeEnhancement).Factor + "x";

    private void ChangeRifeModel(int step)
    {
        var values = RifeEnhancementSettings.ModelValues;
        var current = RifeEnhancementSettings.Normalize(_context.Settings.RifeEnhancement);
        var index = values.ToList().IndexOf(current.Model);
        if (index < 0) index = 0;
        index = (index + step + values.Count) % values.Count;
        _context.SaveSettings(_context.Settings with { RifeEnhancement = current with { Model = values[index] } });
        RefreshValues();
        _shell.ShowToast(AppStrings.Get("tv.settings.nextPlaybackHint"));
    }

    private string ValueRifeModel() => RifeEnhancementSettings.Normalize(_context.Settings.RifeEnhancement).Model;

    private void ChangeRifeHeight(int step)
    {
        var values = RifeEnhancementSettings.ProcessingHeightValues;
        var current = RifeEnhancementSettings.Normalize(_context.Settings.RifeEnhancement);
        var index = values.ToList().IndexOf(current.ProcessingHeight);
        if (index < 0) index = 0;
        index = (index + step + values.Count) % values.Count;
        _context.SaveSettings(_context.Settings with { RifeEnhancement = current with { ProcessingHeight = values[index] } });
        RefreshValues();
        _shell.ShowToast(AppStrings.Get("tv.settings.nextPlaybackHint"));
    }

    private string ValueRifeHeight()
    {
        var height = RifeEnhancementSettings.Normalize(_context.Settings.RifeEnhancement).ProcessingHeight;
        return height == RifeEnhancementSettings.AutoProcessingHeight
            ? AppStrings.Get("quality.rifeHeightAuto")
            : height + "p";
    }

    private void ChangeRifeGpuThread(int step)
    {
        var values = RifeEnhancementSettings.GpuThreadValues;
        var current = RifeEnhancementSettings.Normalize(_context.Settings.RifeEnhancement);
        var index = values.ToList().IndexOf(current.GpuThread);
        if (index < 0) index = 0;
        index = (index + step + values.Count) % values.Count;
        _context.SaveSettings(_context.Settings with { RifeEnhancement = current with { GpuThread = values[index] } });
        RefreshValues();
        _shell.ShowToast(AppStrings.Get("tv.settings.nextPlaybackHint"));
    }

    private string ValueRifeGpuThread() =>
        RifeEnhancementSettings.Normalize(_context.Settings.RifeEnhancement).GpuThread.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void ChangePreset()
    {
        var index = TvKeyMap.PresetIds.ToList().IndexOf(_context.Settings.TvRemotePreset);
        if (index < 0) index = 0;
        var next = TvKeyMap.PresetIds[(index + 1) % TvKeyMap.PresetIds.Count];
        _context.SaveSettings(_context.Settings with { TvRemotePreset = next });
        RefreshValues();
        _shell.ShowToast(AppStrings.Get("tv.settings.restartHint"));
    }

    private void ChangeGamepadLayout()
    {
        var current = TvGamepadFaceLayouts.Normalize(_context.Settings.TvGamepadFaceLayout);
        var next = current == "xbox" ? "nintendo" : "xbox";
        _context.SaveSettings(_context.Settings with { TvGamepadFaceLayout = next });
        _shell.OnTvSettingsChanged();
        RefreshValues();
    }

    private string ValueGamepadLayout() =>
        AppStrings.Get(TvGamepadFaceLayouts.Normalize(_context.Settings.TvGamepadFaceLayout) == "nintendo"
            ? "tv.settings.gamepadLayoutNintendo"
            : "tv.settings.gamepadLayoutXbox");

    private void ChangeGamepadDeadzone(int step)
    {
        var values = new[] { 20, 30, 40, 50, 60, 70, 80 };
        var index = Array.IndexOf(values, _context.Settings.TvGamepadDeadzonePercent);
        if (index < 0) index = 3;
        index = Math.Clamp(index + step, 0, values.Length - 1);
        _context.SaveSettings(_context.Settings with { TvGamepadDeadzonePercent = values[index] });
        _shell.OnTvSettingsChanged();
        RefreshValues();
    }

    private void ChangeExitHold(int step)
    {
        var values = new[] { 1000, 1500, 2000, 3000, 4000 };
        var index = Array.IndexOf(values, _context.Settings.TvExitLongPressMs);
        if (index < 0) index = 2;
        index = Math.Clamp(index + step, 0, values.Length - 1);
        _context.SaveSettings(_context.Settings with { TvExitLongPressMs = values[index] });
        RefreshValues();
        _shell.ShowToast(AppStrings.Get("tv.settings.restartHint"));
    }

    private void RefreshValues()
    {
        foreach (var row in _rows) row.ValueText.Text = row.Value();
    }

    private string ValueEndAction() => AppStrings.Get(_context.Settings.PlaybackEndAction == "play_next_episode"
        ? "tv.settings.endAction.next"
        : "tv.settings.endAction.return");

    private string ValueSubtitle() => _context.Settings.PreferredSubtitleLanguage switch
    {
        "zh_hans" => AppStrings.Get("tv.settings.subtitle.zhHans"),
        "zh_hant" => AppStrings.Get("tv.settings.subtitle.zhHant"),
        "en" => AppStrings.Get("tv.settings.subtitle.en"),
        "ja" => AppStrings.Get("tv.settings.subtitle.ja"),
        _ => AppStrings.Get("tv.settings.subtitle.auto"),
    };

    private string ValuePreset() => _context.Settings.TvRemotePreset switch
    {
        "mce" => AppStrings.Get("tv.settings.preset.mce"),
        "firetv" => AppStrings.Get("tv.settings.preset.firetv"),
        _ => AppStrings.Get("tv.settings.preset.general"),
    };

    private static string Bool(bool value) => AppStrings.Get(value ? "tv.settings.on" : "tv.settings.off");

    private sealed record SettingRow(
        string Id,
        Button Element,
        TextBlock ValueText,
        Func<string> Value,
        Action<int> Adjust,
        bool Focusable);
}