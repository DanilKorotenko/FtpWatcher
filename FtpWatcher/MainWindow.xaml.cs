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
    private readonly DispatcherTimer _watchProgressTimer = new();
    private Uri? _currentDirectoryUri;
    private NetworkCredential? _currentCredentials;
    private bool _isListing;
    private bool _isWatching;
    private DateTime _watchCycleStartUtc;
    private double _watchIntervalSeconds;

    public MainWindow()
    {
        InitializeComponent();
        LoadUserSettings();
        EntriesListView.ItemsSource = _entries;
        _watchTimer.Tick += WatchTimer_Tick;
        _watchProgressTimer.Tick += WatchProgressTimer_Tick;
        _watchProgressTimer.Interval = TimeSpan.FromMilliseconds(100);
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
        _watchIntervalSeconds = seconds;
        _isWatching = true;
        StartWatchButton.Content = "Stop watch";
        SetWatchInputsEnabled(false);
        SetStatus($"Watching FTP every {seconds} second(s)...");
        StartWatchProgress();

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
        StopWatchProgress();
        _isWatching = false;
        StartWatchButton.Content = "Start watch";
        SetWatchInputsEnabled(true);
        SetStatus("Watch stopped.");
    }

    private void StartWatchProgress()
    {
        ResetWatchProgressCycle();
        WatchTimeRemainingTextBlock.Visibility = Visibility.Visible;
        WatchTimeoutProgressBar.Visibility = Visibility.Visible;
        _watchProgressTimer.Start();
    }

    private void StopWatchProgress()
    {
        _watchProgressTimer.Stop();
        WatchTimeRemainingTextBlock.Visibility = Visibility.Collapsed;
        WatchTimeoutProgressBar.Visibility = Visibility.Collapsed;
        WatchTimeoutProgressBar.Value = 0;
    }

    private void ResetWatchProgressCycle()
    {
        _watchCycleStartUtc = DateTime.UtcNow;
        UpdateWatchProgressDisplay();
    }

    private void WatchProgressTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isWatching || _watchIntervalSeconds <= 0)
        {
            return;
        }

        UpdateWatchProgressDisplay();
    }

    private void UpdateWatchProgressDisplay()
    {
        var elapsedSeconds = (DateTime.UtcNow - _watchCycleStartUtc).TotalSeconds;
        var remainingSeconds = Math.Max(0, _watchIntervalSeconds - elapsedSeconds);

        WatchTimeoutProgressBar.Value = Math.Min(100, elapsedSeconds / _watchIntervalSeconds * 100);
        WatchTimeRemainingTextBlock.Text = $"Next refresh in {FormatTimeRemaining(remainingSeconds)}";
    }

    private static string FormatTimeRemaining(double totalSeconds)
    {
        var seconds = (int)Math.Ceiling(totalSeconds);
        if (seconds >= 3600)
        {
            return $"{seconds / 3600}:{seconds % 3600 / 60:D2}:{seconds % 60:D2}";
        }

        if (seconds >= 60)
        {
            return $"{seconds / 60}:{seconds % 60:D2}";
        }

        return $"{seconds} s";
    }

    private async void OpenFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement target } }
            && target.DataContext is FtpEntry entry)
        {
            await NavigateToEntryAsync(entry);
        }
    }

    private async void EntriesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EntriesListView.SelectedItem is FtpEntry entry &&
            (entry.IsParentLink || entry.Type == FtpEntryType.Directory))
        {
            await NavigateToEntryAsync(entry);
        }
    }

    private async Task NavigateToEntryAsync(FtpEntry entry)
    {
        if (_currentDirectoryUri is null)
        {
            SetStatus("List the FTP folder first.", isError: true);
            return;
        }

        Uri? targetUri;
        if (entry.IsParentLink)
        {
            if (!FtpDirectoryClient.TryGetParentDirectoryUri(_currentDirectoryUri, out targetUri))
            {
                SetStatus("Already at the root folder.", isError: true);
                return;
            }
        }
        else if (entry.Type == FtpEntryType.Directory)
        {
            targetUri = FtpDirectoryClient.ToDirectoryUri(_currentDirectoryUri, entry.Name);
        }
        else
        {
            return;
        }

        await NavigateToDirectoryAsync(targetUri!);
    }

    private async Task NavigateToDirectoryAsync(Uri directoryUri)
    {
        ServerAddressTextBox.Text = FtpDirectoryClient.ToDisplayUri(directoryUri);
        await ListDirectoryAsync();
    }

    private async void DeleteEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FtpEntry entry })
        {
            return;
        }

        if (entry.IsParentLink)
        {
            return;
        }

        if (_currentDirectoryUri is null || _currentCredentials is null)
        {
            SetStatus("List the FTP folder first.", isError: true);
            return;
        }

        var confirm = MessageBox.Show(
            "Are you sure?",
            "Confirm delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var deleteButton = sender as Button;
        if (deleteButton is not null)
        {
            deleteButton.IsEnabled = false;
        }

        var isFolder = entry.Type == FtpEntryType.Directory;
        SetStatus(isFolder ? $"Deleting folder {entry.Name}..." : $"Deleting {entry.Name}...");

        try
        {
            await FtpDirectoryClient.DeleteEntryAsync(
                _currentDirectoryUri,
                entry,
                _currentCredentials);

            _entries.Remove(entry);
            var itemLabel = isFolder ? $"folder {entry.Name}" : entry.Name;
            SetStatus($"Deleted {itemLabel} from FTP.");
        }
        catch (WebException webEx) when (webEx.Response is FtpWebResponse ftpResponse)
        {
            SetStatus($"FTP error: {ftpResponse.StatusCode} - {ftpResponse.StatusDescription?.Trim()}", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to delete {entry.Name}: {ex.Message}", isError: true);
        }
        finally
        {
            if (deleteButton is not null)
            {
                deleteButton.IsEnabled = true;
            }
        }
    }

    private async void CopyEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FtpEntry entry })
        {
            return;
        }

        if (entry.IsParentLink)
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
            var directoryUri = FtpDirectoryClient.NormalizeDirectoryUri(ftpUri!);
            var entries = await FtpDirectoryClient.ListDirectoryAsync(directoryUri, credentials);

            _currentDirectoryUri = directoryUri;
            _currentCredentials = credentials;
            ServerAddressTextBox.Text = FtpDirectoryClient.ToDisplayUri(directoryUri);

            if (FtpDirectoryClient.TryGetParentDirectoryUri(directoryUri, out _))
            {
                _entries.Add(new FtpEntry
                {
                    Name = "..",
                    Type = FtpEntryType.Directory,
                    IsParentLink = true
                });
            }

            foreach (var entry in entries.OrderByDescending(x => x.Type == FtpEntryType.Directory)
                                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                _entries.Add(entry);
            }

            var prefix = _isWatching ? "Watch: " : string.Empty;
            var itemCount = _entries.Count(e => !e.IsParentLink);
            var completedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SetStatus($"{prefix}Listed {itemCount} item(s) from {directoryUri} at {completedAt}.");

            if (_isWatching)
            {
                ResetWatchProgressCycle();
            }
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
        _watchProgressTimer.Stop();
        SaveUserSettings();
    }
}
