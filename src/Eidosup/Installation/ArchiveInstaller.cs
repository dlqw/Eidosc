using System.IO.Compression;
using System.Net.Http.Headers;

namespace Eidosup.Installation;

public sealed class ArchiveInstaller
{
    public async Task<string> DownloadAsync(
        string downloadUrl,
        string destinationDirectory,
        string fileName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var targetPath = Path.Combine(destinationDirectory, fileName);
        if (dryRun)
        {
            return targetPath;
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("eidosup", "1.0"));
        using var response = await httpClient.GetAsync(downloadUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(targetPath);
        await source.CopyToAsync(destination, cancellationToken);
        return targetPath;
    }

    public void ExtractZip(string archivePath, string destinationDirectory, bool overwrite, bool dryRun)
    {
        if (dryRun)
        {
            return;
        }

        if (Directory.Exists(destinationDirectory))
        {
            if (!overwrite && Directory.EnumerateFileSystemEntries(destinationDirectory).Any())
            {
                return;
            }

            Directory.Delete(destinationDirectory, recursive: true);
        }

        Directory.CreateDirectory(destinationDirectory);
        ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
    }
}
