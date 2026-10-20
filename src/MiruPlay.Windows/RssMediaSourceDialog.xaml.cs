using System.Windows;

namespace MiruPlay.Windows;

public partial class RssMediaSourceDialog : Window
{
    public RssMediaSourceDialog(string? sourceName = null, string? sourceLocation = null)
    {
        InitializeComponent();
        if (sourceName is not null) SourceNameBox.Text = sourceName;
        if (sourceLocation is not null) LocationBox.Text = sourceLocation;
    }

    public string SourceName => SourceNameBox.Text.Trim();
    public string SourceLocation => LocationBox.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SourceName.Length == 0)
        {
            MessageBox.Show(this, "请输入媒体源名称。", "RSS 在线源", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (SourceLocation.Length == 0)
        {
            MessageBox.Show(this, "请输入 RSS Feed 地址。", "RSS 在线源", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
