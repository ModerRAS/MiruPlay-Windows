using System.Windows;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows;

public partial class WebDavSourceDialog : Window
{
    public WebDavSourceDialog(
        string? sourceName = null,
        string? sourceLocation = null,
        string? username = null)
    {
        InitializeComponent();
        if (sourceName is not null) SourceNameBox.Text = sourceName;
        if (sourceLocation is not null) LocationBox.Text = sourceLocation;
        if (username is not null) UsernameBox.Text = username;
    }

    public string SourceName => SourceNameBox.Text.Trim();
    public string SourceLocation => LocationBox.Text.Trim();
    public string Username => UsernameBox.Text.Trim();

    public string TakePassword()
    {
        var password = PasswordValueBox.Password;
        PasswordValueBox.Clear();
        return password;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SourceName.Length == 0)
        {
            MessageBox.Show(this, "请输入媒体源名称。", "WebDAV", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _ = WebDavMlipClient.NormalizeRoot(SourceLocation);
        }
        catch (ArgumentException error)
        {
            MessageBox.Show(this, error.Message, "WebDAV", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
