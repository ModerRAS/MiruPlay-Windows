using System.Windows;
using MiruPlay.Windows.Services;

namespace MiruPlay.Windows;

public partial class SmbSourceDialog : Window
{
    public SmbSourceDialog(
        string? sourceName = null,
        string? sourceLocation = null,
        string? domain = null,
        string? username = null)
    {
        InitializeComponent();
        if (sourceName is not null) SourceNameBox.Text = sourceName;
        if (sourceLocation is not null) LocationBox.Text = sourceLocation;
        if (domain is not null) DomainBox.Text = domain;
        if (username is not null) UsernameBox.Text = username;
    }

    public string SourceName => SourceNameBox.Text.Trim();
    public string SourceLocation => LocationBox.Text.Trim();
    public string Domain => DomainBox.Text.Trim();
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
            MessageBox.Show(this, "请输入媒体源名称。", "SMB", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _ = SmbPath.NormalizeRoot(SourceLocation);
        }
        catch (ArgumentException error)
        {
            MessageBox.Show(this, error.Message, "SMB", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
