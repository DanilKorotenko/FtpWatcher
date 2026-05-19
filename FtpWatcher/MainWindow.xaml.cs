using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace FtpWatcher;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FtpEntry> _entries = new();

    public MainWindow()
    {
        InitializeComponent();
        EntriesListView.ItemsSource = _entries;
    }

    private async void ListButton_Click(object sender, RoutedEventArgs e)
    {
        await ListDirectoryAsync();
    }

    private async void ServerAddressTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ListDirectoryAsync();
        }
    }

    private async Task ListDirectoryAsync()
    {
        var rawAddress = ServerAddressTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawAddress) || rawAddress.Equals("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Please enter an FTP server address.", isError: true);
            return;
        }

        if (!TryBuildFtpUri(rawAddress, out var ftpUri, out var error))
        {
            SetStatus(error ?? "Invalid FTP address.", isError: true);
            return;
        }

        var username = string.IsNullOrWhiteSpace(UsernameTextBox.Text) ? "anonymous" : UsernameTextBox.Text;
        var password = PasswordBox.Password;
        if (string.Equals(username, "anonymous", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(password))
        {
            password = "anonymous@";
        }

        var credentials = new NetworkCredential(username, password);

        SetBusy(true);
        SetStatus($"Connecting to {ftpUri}...");
        _entries.Clear();

        try
        {
            var entries = await FtpDirectoryClient.ListDirectoryAsync(ftpUri!, credentials);

            foreach (var entry in entries.OrderByDescending(x => x.Type == FtpEntryType.Directory)
                                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                _entries.Add(entry);
            }

            SetStatus($"Listed {_entries.Count} item(s) from {ftpUri}.");
        }
        catch (WebException webEx) when (webEx.Response is FtpWebResponse ftpResponse)
        {
            SetStatus($"FTP error: {ftpResponse.StatusCode} - {ftpResponse.StatusDescription?.Trim()}", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to list folder: {ex.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static bool TryBuildFtpUri(string input, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (!input.Contains("://", StringComparison.Ordinal))
        {
            input = "ftp://" + input;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var parsed))
        {
            error = "The server address is not a valid URI.";
            return false;
        }

        if (parsed.Scheme is not ("ftp" or "ftps"))
        {
            error = "Only ftp:// and ftps:// addresses are supported.";
            return false;
        }

        uri = parsed;
        return true;
    }

    private void SetBusy(bool busy)
    {
        ListButton.IsEnabled = !busy;
        ServerAddressTextBox.IsEnabled = !busy;
        UsernameTextBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        BusyProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError
            ? System.Windows.Media.Brushes.Crimson
            : System.Windows.Media.Brushes.DimGray;
    }
}
