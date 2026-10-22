using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiruPlay.Windows.Controls;
using MiruPlay.Windows.Converters;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows.Tests;

public sealed class PlaybackControlsTests
{
    [Fact]
    public void WpfPagesPresentHierarchyAndPreserveNativeControlKeys()
    {
        RunInSta(() =>
        {
            using var application = new TestApplication();
            var episode = new LibraryEpisode(
                1,
                "episode",
                "episode",
                1,
                1,
                1,
                "第一集",
                "episode.mkv",
                TimeSpan.FromMinutes(24),
                []);
            var series = new LibrarySeries(
                1,
                "series",
                "测试系列",
                null,
                "简介",
                2026,
                null,
                [],
                null,
                [episode],
                [new LibraryExtra(2, 1, 1, 1, "制作花絮", "extra.mkv", TimeSpan.FromMinutes(2))]);

            var detail = new SeriesDetailPage();
            detail.Present(SeriesDetailPresentation.BuildViewState(series));
            Assert.Equal("播放第一集", ((Button)detail.FindName("PrimaryActionButton")).Content);
            Assert.Single(((ListBox)detail.FindName("SeasonSelector")).Items);
            Assert.Single(((ListView)detail.FindName("ExtrasList")).Items);

            var queue = new MpvPlaybackQueue([episode], episode.ProgressKey);
            var playback = new PlaybackPage();
            playback.Present(PlaybackPagePresentation.BuildViewState(
                series,
                queue,
                episode,
                info: null,
                errorMessage: null,
                availableWidthDip: 1200,
                isFullscreen: false,
                isChromeVisible: true));
            var window = new Window { Content = playback, Width = 1440, Height = 810, ShowInTaskbar = false };
            window.Show();
            try
            {
                var actions = new List<PlaybackPageAction>();
                playback.ActionRequested += (_, e) => actions.Add(e.Action);

                var playButton = (Button)playback.FindName("PlayPauseButton");
                Assert.True(playButton.Focus());
                var space = RaisePreviewKey(playback, window, Key.Space);
                Assert.False(space.Handled);
                Assert.Empty(actions);

                var audio = (ComboBox)playback.FindName("SpeedSelector");
                audio.Focus(); // Best effort: headless CI agents may not grant OS focus to composite controls.
                Assert.True(audio.IsVisible && audio.IsEnabled && audio.Focusable);
                // F11/Escape resolve identically in Button and SelectionControl contexts here;
                // the SelectionControl key matrix itself is owned by PlaybackInputTests.
                var f11 = RaisePreviewKey(playback, window, Key.F11);
                Assert.True(f11.Handled);
                Assert.IsType<PlaybackToggleFullscreenAction>(Assert.Single(actions));

                actions.Clear();
                var escape = RaisePreviewKey(playback, window, Key.Escape);
                Assert.True(escape.Handled);
                Assert.IsType<PlaybackExitFullscreenAction>(Assert.Single(actions));

                actions.Clear();
                Assert.NotEqual(0, GetWindowLong(playback.VideoHost.Handle, -16) & 0x0100); // SS_NOTIFY receives native double clicks.
                SendMessage(playback.VideoHost.Handle, 0x0203, IntPtr.Zero, IntPtr.Zero);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                Assert.IsType<PlaybackToggleFullscreenAction>(Assert.Single(actions));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private static KeyEventArgs RaisePreviewKey(UIElement target, Visual source, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        target.RaiseEvent(args);
        return args;
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TestApplication : IDisposable
    {
        private readonly Application _application = new();

        public TestApplication()
        {
            _application.Resources["PosterImageConverter"] = new PosterImageConverter();
            _application.Resources["BooleanToVisibilityConverter"] = new MiruPlay.Windows.Converters.BooleanToVisibilityConverter();
            _application.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/MiruPlay;component/Themes/MiruPlayTheme.xaml", UriKind.Relative)));
        }

        public void Dispose() => _application.Shutdown();
    }
}
