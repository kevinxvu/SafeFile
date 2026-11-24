using SafeFile.Core.Services;

namespace SafeFile.Cli.Infrastructure;

internal static class PathUtil
{
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static string DisplayName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? "root" : name;
    }

    public static bool IsUnder(string root, string path) =>
        ExcludedFolderPathValidator.IsSameOrDescendant(root, path);

    public static bool IsExcluded(string path, IReadOnlyCollection<string> excluded) =>
        excluded.Any(excludedPath => IsUnder(excludedPath, path));

    /// <summary>Mirrors Core's enumeration: skips reparse points and excluded folders.</summary>
    public static IEnumerable<FileInfo> EnumerateRegularFiles(
        DirectoryInfo directory,
        IReadOnlyCollection<string> excluded,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var file in directory.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
                yield return file;
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0 ||
                IsExcluded(child.FullName, excluded))
                continue;
            foreach (var file in EnumerateRegularFiles(child, excluded, cancellationToken))
                yield return file;
        }
    }

    public static long TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Resolves CLI path arguments, failing fast on missing paths.</summary>
    public static IReadOnlyList<string> ResolveExisting(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var raw in paths)
        {
            var full = Normalize(raw);
            if (!File.Exists(full) && !Directory.Exists(full))
                throw new CliException("not_found", $"Path not found: {full}");
            if (!result.Contains(full, PathComparer))
                result.Add(full);
        }
        return result;
    }

    public static IReadOnlyList<string> ValidateExclusions(
        IReadOnlyCollection<string>? excludes,
        IReadOnlyCollection<string> folderRoots) =>
        excludes is null || excludes.Count == 0
            ? []
            : ExcludedFolderPathValidator.MergeAndValidate(excludes, folderRoots, []);

    public static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
