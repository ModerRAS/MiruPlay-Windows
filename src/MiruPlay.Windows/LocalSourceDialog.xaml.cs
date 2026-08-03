using System.Windows;
using Microsoft.Win32;

namespace MiruPlay.Windows;

public partial class LocalSourceDialog : Window
{
    public LocalSourceDialog(string? sourceName = null, string? sourceLocation = null, string recognitionMode = "MLIP")
    {
        InitializeComponent();
        RecognitionModeBox.SelectedItem = RecognitionModeBox.Items.OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, recognitionMode)) ?? RecognitionModeBox.Items[0];
        if (sourceName is not null) SourceNameBox.Text = sourceName;
        if (sourceLocation is not null) LocationBox.Text = sourceLocation;
    }

    public string SourceName => SourceNameBox.Text.Trim();
    public string SourceLocation => LocationBox.Text.Trim();
    public string RecognitionMode => (RecognitionModeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "MLIP";

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择媒体目录",
            InitialDirectory = Directory.Exists(SourceLocation) ? SourceLocation : null,
        };
        if (dialog.ShowDialog(this) == true)
        {
            LocationBox.Text = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(SourceName) || SourceName == "本地媒体")
                SourceNameBox.Text = Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SourceName.Length == 0)
        {
            MessageBox.Show(this, "请输入媒体源名称。", "本地媒体源", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (SourceLocation.Length == 0 || !Directory.Exists(SourceLocation))
        {
            MessageBox.Show(this, "请选择存在的媒体目录。", "本地媒体源", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
