using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MiruPlay.Windows.Models;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows;

public sealed class EpisodeVersionSelectionWindow : Window
{
    public EpisodeVersionSelectionWindow(IReadOnlyList<LibraryEpisode> versions)
    {
        Title = AppStrings.Get("version.nextTitle");
        Width = 460;
        Height = 300;
        MinWidth = 360;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;

        var list = new ListBox
        {
            ItemsSource = versions,
            DisplayMemberPath = nameof(LibraryEpisode.VersionLabel),
            SelectedIndex = 0,
            Margin = new Thickness(16),
        };
        AutomationProperties.SetName(list, AppStrings.Get("wpf.version.label"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 16) };
        var cancel = new Button { Content = AppStrings.Get("wpf.cancel"), MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var play = new Button { Content = AppStrings.Get("wpf.play.play"), MinWidth = 88, IsDefault = true };
        play.Click += (_, _) =>
        {
            if (list.SelectedItem is not LibraryEpisode) return;
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(play);
        var layout = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(list);
        Content = layout;
        Loaded += (_, _) => list.Focus();
        SelectedVersion = () => list.SelectedItem as LibraryEpisode;
    }

    private Func<LibraryEpisode?> SelectedVersion { get; }

    public LibraryEpisode? Version => SelectedVersion();
}
