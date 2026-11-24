using SafeFile.Core.Format;

namespace SafeFile.Cli.Infrastructure;

internal sealed record ScannedVault(
    string Path,
    string RelativeDirectory,
    long Length,
    DateTime LastWriteTimeUtc,
    VaultHeader? Header,
    string? HeaderError);

/// <summary>
/// Finds .safe vaults the same way the desktop Decrypt page does: files are
/// taken as-is, folders are searched recursively and keep their relative layout.
/// </summary>
internal static class VaultScanner
{
    public static IReadOnlyList<ScannedVault> Scan(
        IReadOnlyList<string> inputs,
        IReadOnlyCollection<string> excluded,
        CancellationToken ct)
    {
        var vaults = new List<ScannedVault>();
        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(input))
            {
                foreach (var file in EnumerateVaultFiles(new DirectoryInfo(input), excluded, ct))
                {
                    var directory = System.IO.Path.GetDirectoryName(file) ?? input;
                    var relative = System.IO.Path.GetRelativePath(input, directory);
                    vaults.Add(Read(file, relative == "." ? "" : relative));
                }
            }
            else if (string.Equals(System.IO.Path.GetExtension(input), ".safe", StringComparison.OrdinalIgnoreCase) &&
                     !PathUtil.IsExcluded(input, excluded))
            {
                vaults.Add(Read(input, ""));
            }
            else
            {
                throw new CliException(
                    "invalid_argument",
                    $"Not a .safe vault or folder: {input}",
                    ExitCodes.InvalidUsage);
            }
        }
        return vaults;
    }

    private static IEnumerable<string> EnumerateVaultFiles(
        DirectoryInfo directory,
        IReadOnlyCollection<string> excluded,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var file in directory.EnumerateFiles("*.safe"))
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                !PathUtil.IsExcluded(file.FullName, excluded))
                yield return file.FullName;
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0 ||
                PathUtil.IsExcluded(child.FullName, excluded))
                continue;
            foreach (var file in EnumerateVaultFiles(child, excluded, ct))
                yield return file;
        }
    }

    private static ScannedVault Read(string path, string relativeDirectory)
    {
        VaultHeader? header = null;
        string? error = null;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            header = VaultHeader.ReadFrom(stream);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var info = new FileInfo(path);
        return new ScannedVault(path, relativeDirectory, info.Length, info.LastWriteTimeUtc, header, error);
    }

    public static object DescribeHeader(VaultHeader header) => new
    {
        version = header.Version,
        mode = header.Mode.ToString().ToLowerInvariant(),
        nameMode = header.OutputFileNameMode.ToString().ToLowerInvariant(),
        chunkSizeBytes = header.ChunkSize,
        kdf = new
        {
            algorithm = "argon2id",
            memoryKb = header.KdfParameters.MemorySizeKb,
            iterations = header.KdfParameters.Iterations,
            parallelism = header.KdfParameters.Parallelism
        }
    };
}
