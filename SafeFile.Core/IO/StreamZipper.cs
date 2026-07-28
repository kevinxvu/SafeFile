using System.IO.Compression;

namespace SafeFile.Core.IO;

public sealed class StreamZipper
{
    public static async Task WriteZipStreamAsync(
        string folderPath,
        Stream destination,
        CancellationToken cancellationToken = default,
        Action<int>? bytesRead = null)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        ArgumentNullException.ThrowIfNull(destination);

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        var folderInfo = new DirectoryInfo(folderPath);
        using (var zipArchive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddDirectoryToZipAsync(
                zipArchive, folderInfo, "", bytesRead, cancellationToken).ConfigureAwait(false);
        }
    }

    public static long GetInputSize(string folderPath)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        return GetDirectoryInputSize(new DirectoryInfo(folderPath));
    }

    private static async Task AddDirectoryToZipAsync(
        ZipArchive zipArchive,
        DirectoryInfo directory,
        string entryPrefix,
        Action<int>? bytesRead,
        CancellationToken cancellationToken)
    {
        var files = directory.GetFiles();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(file))
                continue;

            var entryName = string.IsNullOrEmpty(entryPrefix)
                ? file.Name
                : $"{entryPrefix}/{file.Name}";

            var entry = zipArchive.CreateEntry(entryName);
            using (var stream = file.OpenRead())
            using (var entryStream = entry.Open())
            {
                var buffer = new byte[81_920];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    await entryStream.WriteAsync(
                        buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    bytesRead?.Invoke(read);
                }
            }
        }

        var subDirectories = directory.GetDirectories();
        foreach (var subDir in subDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(subDir))
                continue;

            var newPrefix = string.IsNullOrEmpty(entryPrefix)
                ? subDir.Name
                : $"{entryPrefix}/{subDir.Name}";

            await AddDirectoryToZipAsync(
                zipArchive, subDir, newPrefix, bytesRead, cancellationToken).ConfigureAwait(false);
        }
    }

    private static long GetDirectoryInputSize(DirectoryInfo directory)
    {
        long totalBytes = 0;

        foreach (var file in directory.GetFiles())
        {
            if (!IsReparsePoint(file))
                totalBytes = checked(totalBytes + file.Length);
        }

        foreach (var subDirectory in directory.GetDirectories())
        {
            if (!IsReparsePoint(subDirectory))
                totalBytes = checked(totalBytes + GetDirectoryInputSize(subDirectory));
        }

        return totalBytes;
    }

    private static bool IsReparsePoint(FileSystemInfo item) =>
        (item.Attributes & FileAttributes.ReparsePoint) != 0;

    public static async Task ExtractZipStreamAsync(
        Stream zipStream,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zipStream);
        ArgumentNullException.ThrowIfNull(destinationPath);

        if (!Directory.Exists(destinationPath))
            Directory.CreateDirectory(destinationPath);

        var destinationRoot = Path.GetFullPath(destinationPath);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(destinationRoot) + Path.DirectorySeparatorChar;
        using var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in zipArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Zip entry escapes the destination directory: {entry.FullName}");

            var dirPath = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(dirPath) && !Directory.Exists(dirPath))
                Directory.CreateDirectory(dirPath);

            if (!entry.FullName.EndsWith("/"))
            {
                using (var entryStream = entry.Open())
                using (var fileStream = File.Create(fullPath))
                {
                    await entryStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
