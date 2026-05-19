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
}
