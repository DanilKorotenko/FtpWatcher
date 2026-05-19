using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FtpWatcher;

/// <summary>
/// Parses individual lines from an FTP <c>LIST</c> response.
/// Supports the two most common formats: Unix style and MS-DOS style.
/// Falls back to using the line as the entry name if the format is unknown.
/// </summary>
public static class FtpListParser
{
    // Example Unix line:
    //   drwxr-xr-x   2 user group   4096 Jan 12 09:14 folder-name
    //   -rw-r--r--   1 user group  10240 Jan 12  2024 file-name.txt
    private static readonly Regex UnixRegex = new(
        @"^(?<perm>[\-dl])(?<rest_perm>[rwxsStT\-]{9})\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<month>\w{3})\s+(?<day>\d{1,2})\s+(?<yearortime>[\d:]{4,5})\s+(?<name>.+)$",
        RegexOptions.Compiled);

    // Example DOS line:
    //   01-12-25  09:14AM       <DIR>          folder-name
    //   01-12-25  09:14AM               10240  file-name.txt
    private static readonly Regex DosRegex = new(
        @"^(?<date>\d{2}-\d{2}-\d{2,4})\s+(?<time>\d{2}:\d{2}[AP]M)\s+(?<dirOrSize><DIR>|\d+)\s+(?<name>.+)$",
        RegexOptions.Compiled);

    public static FtpEntry Parse(string line)
    {
        line = line.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(line))
        {
            return new FtpEntry { Name = string.Empty, RawLine = line };
        }

        if (TryParseUnix(line, out var unix))
        {
            return unix!;
        }

        if (TryParseDos(line, out var dos))
        {
            return dos!;
        }

        return new FtpEntry
        {
            Name = line.Trim(),
            Type = FtpEntryType.Unknown,
            RawLine = line
        };
    }

    private static bool TryParseUnix(string line, out FtpEntry? entry)
    {
        entry = null;
        var match = UnixRegex.Match(line);
        if (!match.Success)
        {
            return false;
        }

        var permChar = match.Groups["perm"].Value;
        var type = permChar switch
        {
            "d" => FtpEntryType.Directory,
            "l" => FtpEntryType.Symlink,
            "-" => FtpEntryType.File,
            _ => FtpEntryType.Unknown
        };

        long.TryParse(match.Groups["size"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);

        DateTime? modified = TryParseUnixDate(
            match.Groups["month"].Value,
            match.Groups["day"].Value,
            match.Groups["yearortime"].Value);

        var name = match.Groups["name"].Value;
        if (type == FtpEntryType.Symlink)
        {
            var arrowIndex = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIndex > 0)
            {
                name = name[..arrowIndex];
            }
        }

        entry = new FtpEntry
        {
            Name = name,
            Type = type,
            Size = type == FtpEntryType.Directory ? null : size,
            Modified = modified,
            RawLine = line
        };
        return true;
    }

    private static bool TryParseDos(string line, out FtpEntry? entry)
    {
        entry = null;
        var match = DosRegex.Match(line);
        if (!match.Success)
        {
            return false;
        }

        var dirOrSize = match.Groups["dirOrSize"].Value;
        bool isDir = dirOrSize.Equals("<DIR>", StringComparison.OrdinalIgnoreCase);
        long? size = null;
        if (!isDir && long.TryParse(dirOrSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            size = parsed;
        }

        DateTime? modified = null;
        var dateTimeString = $"{match.Groups["date"].Value} {match.Groups["time"].Value}";
        string[] formats = ["MM-dd-yy hh:mmtt", "MM-dd-yyyy hh:mmtt"];
        if (DateTime.TryParseExact(dateTimeString, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            modified = parsedDate;
        }

        entry = new FtpEntry
        {
            Name = match.Groups["name"].Value.Trim(),
            Type = isDir ? FtpEntryType.Directory : FtpEntryType.File,
            Size = size,
            Modified = modified,
            RawLine = line
        };
        return true;
    }

    private static DateTime? TryParseUnixDate(string month, string day, string yearOrTime)
    {
        try
        {
            int monthNumber = DateTime.ParseExact(month, "MMM", CultureInfo.InvariantCulture).Month;
            int dayNumber = int.Parse(day, CultureInfo.InvariantCulture);

            if (yearOrTime.Contains(':'))
            {
                var parts = yearOrTime.Split(':');
                int hour = int.Parse(parts[0], CultureInfo.InvariantCulture);
                int minute = int.Parse(parts[1], CultureInfo.InvariantCulture);

                int year = DateTime.Now.Year;
                var candidate = new DateTime(year, monthNumber, dayNumber, hour, minute, 0);
                // Listings without an explicit year refer to the previous 6 months.
                if (candidate > DateTime.Now.AddDays(1))
                {
                    candidate = candidate.AddYears(-1);
                }
                return candidate;
            }
            else
            {
                int year = int.Parse(yearOrTime, CultureInfo.InvariantCulture);
                return new DateTime(year, monthNumber, dayNumber);
            }
        }
        catch
        {
            return null;
        }
    }
}
