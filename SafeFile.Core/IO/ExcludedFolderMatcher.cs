namespace SafeFile.Core.IO;

internal sealed class ExcludedFolderMatcher
{
    private readonly string[] _paths;

    private ExcludedFolderMatcher(string[] paths) => _paths = paths;

    public static ExcludedFolderMatcher Create(
        string sourceFolderPath,
        IReadOnlyCollection<string>? excludedFolderPaths)
    {
        var sourceRoot = Normalize(sourceFolderPath);
        var paths = (excludedFolderPaths ?? [])
            .Select(Normalize)
            .Distinct(PathComparer)
            .ToArray();
        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Excluded folder not found: {path}");
            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("An excluded folder cannot be a symbolic link, junction, or reparse point.");
            if (!IsStrictDescendant(sourceRoot, path))
                throw new IOException("Every excluded folder must be a descendant of the source folder.");
        }

        var minimal = paths
            .OrderBy(path => path.Length)
            .Where((path, index) => !paths
                .OrderBy(candidate => candidate.Length)
                .Take(index)
                .Any(parent => IsSameOrDescendant(parent, path)))
            .ToArray();
        return new ExcludedFolderMatcher(minimal);
    }

    public bool IsExcluded(DirectoryInfo directory) =>
        _paths.Any(path => IsSameOrDescendant(path, directory.FullName));

    private static bool IsStrictDescendant(string parent, string candidate) =>
        !PathComparer.Equals(Normalize(parent), Normalize(candidate)) &&
        IsSameOrDescendant(parent, candidate);

    private static bool IsSameOrDescendant(string parent, string candidate)
    {
        parent = Normalize(parent);
        candidate = Normalize(candidate);
        if (PathComparer.Equals(parent, candidate))
            return true;
        var relative = Path.GetRelativePath(parent, candidate);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
