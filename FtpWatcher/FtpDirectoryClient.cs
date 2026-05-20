using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace FtpWatcher;

public static class FtpDirectoryClient
{
    /// <summary>
    /// Lists the contents of the directory at <paramref name="ftpUri"/>.
    /// </summary>
    public static async Task<IReadOnlyList<FtpEntry>> ListDirectoryAsync(
        Uri ftpUri,
        NetworkCredential credentials,
        CancellationToken cancellationToken = default)
    {
        if (ftpUri.Scheme is not ("ftp" or "ftps"))
        {
            throw new ArgumentException("URI must use the ftp or ftps scheme.", nameof(ftpUri));
        }

#pragma warning disable SYSLIB0014 // FtpWebRequest is obsolete but still functional in .NET 9
        var request = (FtpWebRequest)WebRequest.Create(ftpUri);
#pragma warning restore SYSLIB0014
        request.Method = WebRequestMethods.Ftp.ListDirectoryDetails;
        request.Credentials = credentials;
        request.UseBinary = true;
        request.UsePassive = true;
        request.KeepAlive = false;
        request.EnableSsl = ftpUri.Scheme == "ftps";

        using var registration = cancellationToken.Register(() =>
        {
            try { request.Abort(); } catch { /* ignore */ }
        });

        using var response = (FtpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
        using var stream = response.GetResponseStream();
        using var reader = new StreamReader(stream);

        var results = new List<FtpEntry>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = FtpListParser.Parse(line);
            if (string.IsNullOrEmpty(entry.Name) || entry.Name is "." or "..")
            {
                continue;
            }

            results.Add(entry);
        }

        return results;
    }

    public static async Task DownloadFileAsync(
        Uri fileUri,
        NetworkCredential credentials,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        if (fileUri.Scheme is not ("ftp" or "ftps"))
        {
            throw new ArgumentException("URI must use the ftp or ftps scheme.", nameof(fileUri));
        }

        var directory = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

#pragma warning disable SYSLIB0014
        var request = (FtpWebRequest)WebRequest.Create(fileUri);
#pragma warning restore SYSLIB0014
        request.Method = WebRequestMethods.Ftp.DownloadFile;
        request.Credentials = credentials;
        request.UseBinary = true;
        request.UsePassive = true;
        request.KeepAlive = false;
        request.EnableSsl = fileUri.Scheme == "ftps";

        using var registration = cancellationToken.Register(() =>
        {
            try { request.Abort(); } catch { /* ignore */ }
        });

        using var response = (FtpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
        await using var ftpStream = response.GetResponseStream();
        await using var fileStream = new FileStream(
            localFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await ftpStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a file or recursively downloads a directory into <paramref name="destinationFolder"/>.
    /// </summary>
    /// <returns>Number of files downloaded.</returns>
    public static async Task<int> DownloadEntryAsync(
        Uri parentDirectoryUri,
        FtpEntry entry,
        NetworkCredential credentials,
        string destinationFolder,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);

        return entry.Type switch
        {
            FtpEntryType.Directory => await DownloadDirectoryRecursiveAsync(
                ToDirectoryUri(parentDirectoryUri, entry.Name),
                credentials,
                Path.Combine(destinationFolder, entry.Name),
                cancellationToken,
                progress).ConfigureAwait(false),

            FtpEntryType.File => await DownloadSingleFileAsync(
                parentDirectoryUri,
                entry.Name,
                credentials,
                Path.Combine(destinationFolder, entry.Name),
                cancellationToken,
                progress).ConfigureAwait(false),

            FtpEntryType.Symlink or FtpEntryType.Unknown => await TryDownloadAsFileAsync(
                parentDirectoryUri,
                entry.Name,
                credentials,
                Path.Combine(destinationFolder, entry.Name),
                cancellationToken,
                progress).ConfigureAwait(false),

            _ => throw new NotSupportedException($"Cannot download entry type '{entry.Type}'.")
        };
    }

    private static async Task<int> DownloadDirectoryRecursiveAsync(
        Uri directoryUri,
        NetworkCredential credentials,
        string localDirectoryPath,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        Directory.CreateDirectory(localDirectoryPath);

        var entries = await ListDirectoryAsync(directoryUri, credentials, cancellationToken)
            .ConfigureAwait(false);

        var fileCount = 0;
        foreach (var child in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (child.Type == FtpEntryType.Directory)
            {
                fileCount += await DownloadDirectoryRecursiveAsync(
                    ToDirectoryUri(directoryUri, child.Name),
                    credentials,
                    Path.Combine(localDirectoryPath, child.Name),
                    cancellationToken,
                    progress).ConfigureAwait(false);
            }
            else if (child.Type == FtpEntryType.File)
            {
                fileCount += await DownloadSingleFileAsync(
                    directoryUri,
                    child.Name,
                    credentials,
                    Path.Combine(localDirectoryPath, child.Name),
                    cancellationToken,
                    progress).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    fileCount += await TryDownloadAsFileAsync(
                        directoryUri,
                        child.Name,
                        credentials,
                        Path.Combine(localDirectoryPath, child.Name),
                        cancellationToken,
                        progress).ConfigureAwait(false);
                }
                catch (WebException)
                {
                    // Skip entries that cannot be downloaded as files.
                }
            }
        }

        return fileCount;
    }

    private static async Task<int> DownloadSingleFileAsync(
        Uri parentDirectoryUri,
        string fileName,
        NetworkCredential credentials,
        string localFilePath,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        progress?.Report(fileName);
        var fileUri = new Uri(parentDirectoryUri, fileName);
        await DownloadFileAsync(fileUri, credentials, localFilePath, cancellationToken).ConfigureAwait(false);
        return 1;
    }

    private static async Task<int> TryDownloadAsFileAsync(
        Uri parentDirectoryUri,
        string name,
        NetworkCredential credentials,
        string localFilePath,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        try
        {
            return await DownloadSingleFileAsync(
                parentDirectoryUri,
                name,
                credentials,
                localFilePath,
                cancellationToken,
                progress).ConfigureAwait(false);
        }
        catch (WebException ex) when (ex.Response is FtpWebResponse { StatusCode: FtpStatusCode.ActionNotTakenFileUnavailable or FtpStatusCode.ActionNotTakenFileUnavailableOrBusy })
        {
            throw new NotSupportedException($"Cannot download '{name}': not a downloadable file.", ex);
        }
    }

    private static Uri ToDirectoryUri(Uri parentUri, string directoryName)
    {
        var combined = new Uri(parentUri, directoryName);
        var absolute = combined.AbsoluteUri;
        return absolute.EndsWith("/", StringComparison.Ordinal)
            ? combined
            : new Uri(absolute + "/");
    }
}
