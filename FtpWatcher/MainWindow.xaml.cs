using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FtpWatcher;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FtpEntry> _entries = new();
    private readonly DispatcherTimer _watchTimer = new();
    private Uri? _currentDirectoryUri;
    private NetworkCredential? _currentCredentials;
    private bool _isListing;
    private bool _isWatching;

    public MainWindow()
    {
        InitializeComponent();
        LoadUserSettings();
        EntriesListView.ItemsSource = _entries;
        _watchTimer.Tick += WatchTimer_Tick;
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

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose destination folder",
            InitialDirectory = string.IsNullOrWhiteSpace(DestinationFolderTextBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : DestinationFolderTextBox.Text
        };

        if (dialog.ShowDialog() == true)
        {
            DestinationFolderTextBox.Text = dialog.FolderName;
            SaveUserSettings();
        }
    }

    private void RefreshTimeoutTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"^\d+$");
    }

    private void RefreshTimeoutTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!TryGetRefreshTimeoutSeconds(out _))
        {
            RefreshTimeoutTextBox.Text = "60";
        }
    }

    private async void StartWatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isWatching)
        {
            StopWatch();
            return;
        }

        if (!TryBuildFtpUri(ServerAddressTextBox.Text?.Trim() ?? string.Empty, out _, out var error))
        {
            SetStatus(error ?? "Invalid FTP address.", isError: true);
            return;
        }

        if (!TryGetRefreshTimeoutSeconds(out var seconds))
        {
            SetStatus("Refresh timeout must be a positive number of seconds.", isError: true);
            return;
        }

        _watchTimer.Interval = TimeSpan.FromSeconds(seconds);
        _isWatching = true;
        StartWatchButton.Content = "Stop watch";
        SetWatchInputsEnabled(false);
        SetStatus($"Watching FTP every {seconds} second(s)...");

        await ListDirectoryAsync(fromWatch: true);
        _watchTimer.Start();
    }

    private async void WatchTimer_Tick(object? sender, EventArgs e)
    {
        if (_isListing)
        {
            return;
        }

        await ListDirectoryAsync(fromWatch: true);
    }

    private void StopWatch()
    {
        _watchTimer.Stop();
        _isWatching = false;
        StartWatchButton.Content = "Start watch";
        SetWatchInputsEnabled(true);
        SetStatus("Watch stopped.");
    }

    private async void CopyEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FtpEntry entry })
        {
            return;
        }

        if (!TryGetDestinationFolder(out var destinationFolder, out var error))
        {
            SetStatus(error!, isError: true);
            return;
        }

        if (_currentDirectoryUri is null || _currentCredentials is null)
        {
            SetStatus("List the FTP folder first.", isError: true);
            return;
        }

        var copyButton = sender as Button;
        if (copyButton is not null)
        {
            copyButton.IsEnabled = false;
        }

        var isFolder = entry.Type == FtpEntryType.Directory;
        SetStatus(isFolder ? $"Copying folder {entry.Name}..." : $"Copying {entry.Name}...");

        var progress = new Progress<string>(current =>
        {
            var prefix = isFolder ? $"Copying folder {entry.Name}" : $"Copying {entry.Name}";
            SetStatus($"{prefix}: {current}");
        });

        try
        {
            var fileCount = await FtpDirectoryClient.DownloadEntryAsync(
                _currentDirectoryUri,
                entry,
                _currentCredentials,
                destinationFolder,
                progress: progress);

            var itemLabel = isFolder ? $"folder {entry.Name}" : entry.Name;
            SetStatus($"Copied {itemLabel} ({fileCount} file(s)) to {destinationFolder}.");
        }
        catch (WebException webEx) when (webEx.Response is FtpWebResponse ftpResponse)
        {
            SetStatus($"FTP error: {ftpResponse.StatusCode} - {ftpResponse.StatusDescription?.Trim()}", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to copy {entry.Name}: {ex.Message}", isError: true);
        }
        finally
        {
            if (copyButton is not null)
            {
                copyButton.IsEnabled = true;
            }
        }
    }

    private async Task ListDirectoryAsync(bool fromWatch = false)
    {
        if (_isListing)
        {
            return;
        }

        var rawAddress = ServerAddressTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawAddress) || rawAddress.Equals("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            if (!fromWatch)
            {
                SetStatus("Please enter an FTP server address.", isError: true);
            }

            return;
        }

        if (!TryBuildFtpUri(rawAddress, out var ftpUri, out var error))
        {
            if (!fromWatch)
            {
                SetStatus(error ?? "Invalid FTP address.", isError: true);
            }

            return;
        }

        if (!TryGetCredentials(out var credentials))
        {
            return;
        }

        _isListing = true;
        SetBusy(true);
        if (!fromWatch)
        {
            SetStatus($"Connecting to {ftpUri}...");
        }

        _entries.Clear();

        try
        {
            var entries = await FtpDirectoryClient.ListDirectoryAsync(ftpUri!, credentials);

            _currentDirectoryUri = ftpUri;
            _currentCredentials = credentials;

            foreach (var entry in entries.OrderByDescending(x => x.Type == FtpEntryType.Directory)
                                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                _entries.Add(entry);
            }

            var prefix = _isWatching ? "Watch: " : string.Empty;
            SetStatus($"{prefix}Listed {_entries.Count} item(s) from {ftpUri}.");
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
            _isListing = false;
            SetBusy(false);
        }
    }

    private bool TryGetCredentials(out NetworkCredential credentials)
    {
        var username = string.IsNullOrWhiteSpace(UsernameTextBox.Text) ? "anonymous" : UsernameTextBox.Text;
        var password = PasswordBox.Password;
        if (string.Equals(username, "anonymous", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(password))
        {
            password = "anonymous@";
        }

        credentials = new NetworkCredential(username, password);
        return true;
    }

    private bool TryGetDestinationFolder(out string folder, out string? error)
    {
        folder = DestinationFolderTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(folder))
        {
            error = "Please choose a destination folder.";
            return false;
        }

        if (!Directory.Exists(folder))
        {
            error = "Destination folder does not exist.";
            return false;
        }

        error = null;
        return true;
    }

    private bool TryGetRefreshTimeoutSeconds(out int seconds)
    {
        seconds = 0;
        var text = RefreshTimeoutTextBox.Text?.Trim() ?? string.Empty;
        if (!int.TryParse(text, out seconds) || seconds < 1)
        {
            return false;
        }

        return true;
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
        ServerAddressTextBox.IsEnabled = !busy && !_isWatching;
        UsernameTextBox.IsEnabled = !busy && !_isWatching;
        PasswordBox.IsEnabled = !busy && !_isWatching;
        ChooseFolderButton.IsEnabled = !busy;
        DestinationFolderTextBox.IsEnabled = !busy;
        BusyProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetWatchInputsEnabled(bool enabled)
    {
        RefreshTimeoutTextBox.IsEnabled = enabled;
        StartWatchButton.IsEnabled = true;
        ListButton.IsEnabled = enabled;
        ServerAddressTextBox.IsEnabled = enabled;
        UsernameTextBox.IsEnabled = enabled;
        PasswordBox.IsEnabled = enabled;
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError
            ? System.Windows.Media.Brushes.Crimson
            : System.Windows.Media.Brushes.DimGray;
    }

    private void LoadUserSettings()
    {
        var settings = UserSettingsStore.Load();

        ServerAddressTextBox.Text = settings.ServerAddress;
        UsernameTextBox.Text = settings.Username;
        PasswordBox.Password = settings.Password;
        DestinationFolderTextBox.Text = settings.DestinationFolder;
        RefreshTimeoutTextBox.Text = settings.RefreshTimeoutSeconds;

        if (!TryGetRefreshTimeoutSeconds(out _))
        {
            RefreshTimeoutTextBox.Text = "60";
        }
    }

    private void SaveUserSettings()
    {
        if (!TryGetRefreshTimeoutSeconds(out _))
        {
            RefreshTimeoutTextBox.Text = "60";
        }

        UserSettingsStore.Save(new UserSettings
        {
            ServerAddress = ServerAddressTextBox.Text ?? string.Empty,
            Username = UsernameTextBox.Text ?? string.Empty,
            Password = PasswordBox.Password,
            DestinationFolder = DestinationFolderTextBox.Text ?? string.Empty,
            RefreshTimeoutSeconds = RefreshTimeoutTextBox.Text ?? "60"
        });
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _watchTimer.Stop();
        SaveUserSettings();
    }
}
