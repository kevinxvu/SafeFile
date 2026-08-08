using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SafeFile.Services;

internal static class ExcludedFolderPathValidator
{
    public static IReadOnlyList<string> MergeAndValidate(
        IEnumerable<string> selectedPaths,
        IEnumerable<string> sourceRoots,
        IEnumerable<string> existingPaths)
    {
        var roots = sourceRoots
            .Where(Directory.Exists)
            .Select(Normalize)
            .Distinct(PathComparer)
            .ToArray();
        if (roots.Length == 0)
            throw new ExcludedFolderValidationException("ExcludeSelectSourceFirst");

        var selected = selectedPaths.Select(Normalize).Distinct(PathComparer).ToArray();
        foreach (var path in selected)
        {
            if (!Directory.Exists(path))
                throw new ExcludedFolderValidationException("ExcludeFolderNotFound");
            if ((new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ExcludedFolderValidationException("ExcludeReparseNotAllowed");
            if (roots.Any(root => PathComparer.Equals(root, path)))
                throw new ExcludedFolderValidationException("ExcludeSourceRootNotAllowed");
            if (!roots.Any(root => IsStrictDescendant(root, path)))
                throw new ExcludedFolderValidationException("ExcludeOutsideSource");
        }

        var combined = existingPaths
            .Concat(selected)
            .Select(Normalize)
            .Distinct(PathComparer)
            .OrderBy(path => path.Length)
            .ToList();
        var minimal = new List<string>();
        foreach (var path in combined)
        {
            if (!minimal.Any(parent => IsSameOrDescendant(parent, path)))
                minimal.Add(path);
        }

        return minimal;
    }

    public static bool IsSameOrDescendant(string parentPath, string candidatePath)
    {
        var parent = Normalize(parentPath);
        var candidate = Normalize(candidatePath);
        if (PathComparer.Equals(parent, candidate))
            return true;
        var relative = Path.GetRelativePath(parent, candidate);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private static bool IsStrictDescendant(string rootPath, string candidatePath) =>
        !PathComparer.Equals(Normalize(rootPath), Normalize(candidatePath)) &&
        IsSameOrDescendant(rootPath, candidatePath);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

internal sealed class ExcludedFolderValidationException(string resourceKey) : IOException
{
    public string ResourceKey { get; } = resourceKey;
}
