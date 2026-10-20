using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows;

public sealed class MusicLibraryWindow : Window
{
    private readonly MusicLibraryService _library;
    private readonly Func<long, string?> _getRoot;
    private readonly Func<long, IReadOnlyList<WebControlServer.MusicPlayRequest>, int, Task<bool>> _play;
    private readonly ComboBox _sources = new() { MinWidth = 260 };
    private readonly ListBox _albums = new() { MinHeight = 120, MaxHeight = 220, DisplayMemberPath = nameof(AlbumItem.Label) };
    private readonly ListBox _tracks = new() { MinHeight = 140, DisplayMemberPath = nameof(TrackItem.Label) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _playTrackButton = new() { Content = "播放所选曲目", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _playAlbumButton = new() { Content = "从该曲开始连播整张专辑" };
    private IReadOnlyList<MusicLibraryService.MusicAlbum> _loadedAlbums = [];
    private long _sourceId;

    public MusicLibraryWindow(
        IReadOnlyList<MediaSourceDefinition> localSources,
        MusicLibraryService library,
        Func<long, string?> getRoot,
        Func<long, IReadOnlyList<WebControlServer.MusicPlayRequest>, int, Task<bool>> play)
    {
        _library = library;
        _getRoot = getRoot;
        _play = play;
        Title = "音乐库";
        Width = 720;
        Height = 560;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(18) };
        var sourceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        sourceRow.Children.Add(new TextBlock { Text = "本地来源", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        _sources.ItemsSource = localSources;
        _sources.DisplayMemberPath = nameof(MediaSourceDefinition.Name);
        _sources.SelectedIndex = localSources.Count > 0 ? 0 : -1;
        AutomationProperties.SetName(_sources, "音乐来源");
        _sources.SelectionChanged += Sources_SelectionChanged;
        sourceRow.Children.Add(_sources);
        root.Children.Add(sourceRow);

        root.Children.Add(new TextBlock { Text = "专辑", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        AutomationProperties.SetName(_albums, "专辑列表");
        _albums.SelectionChanged += Albums_SelectionChanged;
        root.Children.Add(_albums);

        root.Children.Add(new TextBlock { Text = "曲目", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        AutomationProperties.SetName(_tracks, "曲目列表");
        _tracks.MouseDoubleClick += async (_, _) => await PlaySelectedAsync(fromStart: false);
        root.Children.Add(_tracks);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(_playTrackButton, "播放所选曲目");
        _playTrackButton.Click += async (_, _) => await PlaySelectedAsync(fromStart: false);
        AutomationProperties.SetName(_playAlbumButton, "从该曲开始连播整张专辑");
        _playAlbumButton.Click += async (_, _) => await PlaySelectedAsync(fromStart: true);
        var close = new Button { Content = "关闭", Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(_playTrackButton);
        buttons.Children.Add(_playAlbumButton);
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        if (_sources.SelectedItem is MediaSourceDefinition selected) _ = LoadAsync(selected.Id);
        else _status.Text = "没有本地来源；请先在媒体源中添加本地目录。";
    }

    private async void Sources_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sources.SelectedItem is MediaSourceDefinition selected) await LoadAsync(selected.Id);
    }

    private void Albums_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_albums.SelectedItem is not AlbumItem album) return;
        _tracks.ItemsSource = album.Tracks
            .Select((track, index) => new TrackItem(index, track))
            .ToList();
        _tracks.SelectedIndex = 0;
        _status.Text = $"{album.Label}（{album.Tracks.Count} 首）";
    }

    private async Task LoadAsync(long sourceId)
    {
        _sourceId = sourceId;
        _status.Text = "正在扫描音乐目录…";
        _albums.ItemsSource = null;
        _tracks.ItemsSource = null;
        try
        {
            var root = _getRoot(sourceId);
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidDataException("来源路径不可用。");
            var albums = await Task.Run(() => _library.Scan(root));
            _loadedAlbums = albums;
            _albums.ItemsSource = albums
                .Select(album => new AlbumItem(album.Title, album.Tracks))
                .ToList();
            _albums.SelectedIndex = _albums.Items.Count > 0 ? 0 : -1;
            _status.Text = albums.Count == 0 ? "该来源没有可播放的音频文件。" : $"共 {albums.Count} 张专辑。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            _status.Text = error.Message;
        }
    }

    private async Task PlaySelectedAsync(bool fromStart)
    {
        if (_albums.SelectedItem is not AlbumItem album) return;
        if (_tracks.SelectedItem is not TrackItem selected && !fromStart)
        {
            _status.Text = "请先选择一首曲目。";
            return;
        }
        var requests = album.Tracks.Select(track => new WebControlServer.MusicPlayRequest(
            _sourceId,
            track.FilePath,
            track.Number,
            string.IsNullOrWhiteSpace(track.Performer) ? track.Title : $"{track.Title} · {track.Performer}",
            track.StartMs,
            track.EndMs)).ToList();
        var index = fromStart ? 0 : _tracks.SelectedIndex;
        if (index < 0) index = 0;
        var ok = await _play(_sourceId, requests, index);
        _status.Text = ok ? $"正在播放：{requests[index].Title}" : "播放启动失败；请检查文件是否存在。";
    }

    private sealed record AlbumItem(string Label, IReadOnlyList<MusicLibraryService.MusicTrack> Tracks);
    private sealed record TrackItem(int Index, MusicLibraryService.MusicTrack Track)
    {
        public string Label => $"{Track.Number:00} · {Track.Title}" +
            (Track.EndMs > Track.StartMs
                ? $"（{TimeSpan.FromMilliseconds(Track.EndMs - Track.StartMs):m\\:ss}）"
                : string.Empty);
    }
}
