using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FtpWatcher;

public enum FtpEntryType
{
    File,
    Directory,
    Symlink,
    Unknown
}

public class FtpEntry : INotifyPropertyChanged
{
    private bool _isCopied;

    public string Name { get; init; } = string.Empty;
    public FtpEntryType Type { get; init; } = FtpEntryType.Unknown;
    public long? Size { get; init; }
    public DateTime? Modified { get; init; }
    public string? RawLine { get; init; }
    public bool IsParentLink { get; init; }

    public bool IsCopied
    {
        get => _isCopied;
        set
        {
            if (_isCopied == value)
            {
                return;
            }

            _isCopied = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public bool IsNavigationEntry => IsParentLink || Type == FtpEntryType.Directory;

    public string TypeLabel => IsParentLink
        ? string.Empty
        : Type switch
        {
            FtpEntryType.Directory => "Folder",
            FtpEntryType.File => "File",
            FtpEntryType.Symlink => "Link",
            _ => string.Empty
        };

    public string SizeLabel
    {
        get
        {
            if (Type == FtpEntryType.Directory || Size is null)
            {
                return string.Empty;
            }

            return FormatSize(Size.Value);
        }
    }

    public string ModifiedLabel => Modified?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} {units[unit]}"
            : $"{size:N1} {units[unit]}";
    }
}
