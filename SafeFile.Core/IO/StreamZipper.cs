using System.IO.Compression;

namespace SafeFile.Core.IO;

public sealed class StreamZipper
{
    public static async Task<Stream> CreateZipStreamAsync(string folderPath)
    {
        ArgumentNullException.ThrowIfNull(folderPath);

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        var memoryStream = new MemoryStream();
        var folderInfo = new DirectoryInfo(folderPath);

        using (var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddDirectoryToZipAsync(zipArchive, folderInfo, "");
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    private static async Task AddDirectoryToZipAsync(ZipArchive zipArchive, DirectoryInfo directory, string entryPrefix)
    {
        var files = directory.GetFiles();
        foreach (var file in files)
        {
            var entryName = string.IsNullOrEmpty(entryPrefix)
                ? file.Name
                : $"{entryPrefix}/{file.Name}";

            var entry = zipArchive.CreateEntry(entryName);
            using (var stream = file.OpenRead())
            using (var entryStream = entry.Open())
            {
                await stream.CopyToAsync(entryStream).ConfigureAwait(false);
            }
        }

        var subDirectories = directory.GetDirectories();
        foreach (var subDir in subDirectories)
        {
            var newPrefix = string.IsNullOrEmpty(entryPrefix)
                ? subDir.Name
                : $"{entryPrefix}/{subDir.Name}";

            await AddDirectoryToZipAsync(zipArchive, subDir, newPrefix).ConfigureAwait(false);
        }
    }

    public static async Task ExtractZipStreamAsync(Stream zipStream, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(zipStream);
        ArgumentNullException.ThrowIfNull(destinationPath);

        if (!Directory.Exists(destinationPath))
            Directory.CreateDirectory(destinationPath);

        using var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        foreach (var entry in zipArchive.Entries)
        {
            var fullPath = Path.Combine(destinationPath, entry.FullName);
            var dirPath = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(dirPath) && !Directory.Exists(dirPath))
                Directory.CreateDirectory(dirPath);

            if (!entry.FullName.EndsWith("/"))
            {
                using (var entryStream = entry.Open())
                using (var fileStream = File.Create(fullPath))
                {
                    await entryStream.CopyToAsync(fileStream).ConfigureAwait(false);
                }
            }
        }
    }
}
