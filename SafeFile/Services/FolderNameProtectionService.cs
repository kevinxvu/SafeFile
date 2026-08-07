using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SafeFile.Core.Services;

namespace SafeFile.Services;

public enum FolderNameProtectionMode
{
    Aes,
    Sha256
}

public enum FolderNameEntryState
{
    Original,
    Protected,
    Conflict,
    Stale
}

public sealed record FolderNameScanResult(
    string RootPath,
    bool HasManifest,
    int PhysicalFolderCount);

public sealed record FolderNameProgress(
    string CurrentPath,
    int Completed,
    int Total);

public sealed class FolderNameSession
{
    internal FolderNameSession(
        string rootPath,
        FolderNameManifest manifest,
        IReadOnlyList<FolderNameNode> nodes,
        bool needsManifestRewrite)
    {
        RootPath = rootPath;
        Mode = manifest.NameMode;
        Manifest = manifest;
        Nodes = nodes;
        NeedsManifestRewrite = needsManifestRewrite;
    }

    public string RootPath { get; }
    public FolderNameProtectionMode Mode { get; }
    public int TotalCount => Nodes.Count(node => node.State != FolderNameEntryState.Stale);
    public int ProtectedCount => Nodes.Count(node => node.State == FolderNameEntryState.Protected);
    public int ClearCount => Nodes.Count(node => node.State == FolderNameEntryState.Original);
    public int ConflictCount => Nodes.Count(node => node.State == FolderNameEntryState.Conflict);
    public int NewCount => Nodes.Count(node => node.IsNew && node.State == FolderNameEntryState.Original);

    internal FolderNameManifest Manifest { get; }
    internal IReadOnlyList<FolderNameNode> Nodes { get; }
    internal bool NeedsManifestRewrite { get; }
}

public sealed class FolderNameProtectionService
{
    public const string ManifestFileName = ".safefile-names";
    private const string ManifestFormat = "SafeFileFolderMap";
    private const int ManifestVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly TextCryptoService _textCryptoService;
    private readonly ILogger<FolderNameProtectionService> _logger;

    public FolderNameProtectionService(
        TextCryptoService textCryptoService,
        ILogger<FolderNameProtectionService> logger)
    {
        _textCryptoService = textCryptoService;
        _logger = logger;
    }

    public Task<FolderNameScanResult> ScanAsync(
        string rootPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var root = ValidateRoot(rootPath);
            var count = EnumerateDirectories(root, cancellationToken).Count();
            return new FolderNameScanResult(
                root.FullName,
                File.Exists(GetManifestPath(root.FullName)),
                count);
        }, cancellationToken);

    public Task<FolderNameSession> CreateSessionAsync(
        string rootPath,
        FolderNameProtectionMode mode,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var root = ValidateRoot(rootPath);
            if (File.Exists(GetManifestPath(root.FullName)))
                throw new InvalidOperationException("A folder-name manifest already exists. Verify it before continuing.");

            var manifest = new FolderNameManifest
            {
                Format = ManifestFormat,
                Version = ManifestVersion,
                NameMode = mode
            };
            return Analyze(root, manifest, includeNewDirectories: true, cancellationToken);
        }, cancellationToken);

    public async Task<FolderNameSession> VerifyManifestAsync(
        string rootPath,
        byte[] passwordBytes,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateRoot(rootPath);
        var manifestPath = GetManifestPath(root.FullName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The folder-name manifest was not found.", manifestPath);

        var encrypted = await File.ReadAllTextAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        var json = await _textCryptoService.DecryptAsync(
                encrypted,
                passwordBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<FolderNameManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("The folder-name manifest is empty.");
        ValidateManifest(manifest);

        return await Task.Run(
            () => Analyze(root, manifest, includeNewDirectories: true, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task EncryptAsync(
        FolderNameSession session,
        byte[] passwordBytes,
        IProgress<FolderNameProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureNoConflicts(session);

        var activeEntries = session.Nodes
            .Where(node => node.State != FolderNameEntryState.Stale)
            .Select(node => node.Entry)
            .DistinctBy(entry => entry.OriginalPath, PathComparer)
            .OrderBy(entry => GetDepth(entry.OriginalPath))
            .ToList();
        session.Manifest.Directories = activeEntries;
        await WriteManifestAsync(
            session.RootPath,
            session.Manifest,
            passwordBytes,
            cancellationToken).ConfigureAwait(false);

        var pending = session.Nodes
            .Where(node => node.State == FolderNameEntryState.Original)
            .OrderByDescending(node => node.Depth)
            .ToArray();
        await RenameNodesAsync(
            pending,
            encrypt: true,
            progress,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Folder-name encryption completed using {Mode}; renamed {FolderCount} folders",
            session.Mode,
            pending.Length);
    }

    public async Task DecryptAsync(
        FolderNameSession session,
        byte[] passwordBytes,
        IProgress<FolderNameProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureNoConflicts(session);

        if (session.NeedsManifestRewrite)
        {
            await WriteManifestAsync(
                session.RootPath,
                session.Manifest,
                passwordBytes,
                cancellationToken).ConfigureAwait(false);
        }

        var pending = session.Nodes
            .Where(node => !node.IsNew && node.State == FolderNameEntryState.Protected)
            .OrderByDescending(node => node.Depth)
            .ToArray();
        await RenameNodesAsync(
            pending,
            encrypt: false,
            progress,
            cancellationToken).ConfigureAwait(false);

        var refreshed = Analyze(
            ValidateRoot(session.RootPath),
            session.Manifest,
            includeNewDirectories: false,
            cancellationToken);
        if (refreshed.ProtectedCount == 0 && refreshed.ConflictCount == 0)
            File.Delete(GetManifestPath(session.RootPath));

        _logger.LogInformation(
            "Folder-name decryption completed; renamed {FolderCount} folders",
            pending.Length);
    }

    private static FolderNameSession Analyze(
        DirectoryInfo root,
        FolderNameManifest manifest,
        bool includeNewDirectories,
        CancellationToken cancellationToken)
    {
        ValidateManifest(manifest);
        var rootName = root.Name;
        var needsRewrite = RebaseRoot(manifest, rootName);
        var entries = manifest.Directories
            .OrderBy(entry => GetDepth(entry.OriginalPath))
            .ToList();
        var originalPaths = new HashSet<string>(
            entries.Select(entry => entry.OriginalPath),
            PathComparer);
        var nodes = new List<FolderNameNode>();

        AnalyzeChildren(
            root,
            rootName,
            rootName,
            entries,
            originalPaths,
            nodes,
            manifest.NameMode,
            includeNewDirectories,
            cancellationToken);

        manifest.Directories = entries
            .DistinctBy(entry => entry.OriginalPath, PathComparer)
            .OrderBy(entry => GetDepth(entry.OriginalPath))
            .ToList();
        return new FolderNameSession(root.FullName, manifest, nodes, needsRewrite);
    }

    private static void AnalyzeChildren(
        DirectoryInfo actualParent,
        string logicalOriginalParent,
        string logicalProtectedParent,
        List<FolderNameMapEntry> entries,
        HashSet<string> originalPaths,
        List<FolderNameNode> nodes,
        FolderNameProtectionMode mode,
        bool includeNewDirectories,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var physicalChildren = EnumerateDirectDirectories(actualParent).ToArray();
        var consumed = new HashSet<string>(PathComparer);
        var mappedChildren = entries
            .Where(entry => string.Equals(
                GetParent(entry.OriginalPath),
                logicalOriginalParent,
                PathComparison))
            .ToArray();
        var hasMissingMapping = false;

        foreach (var entry in mappedChildren)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var originalSegment = GetLastSegment(entry.OriginalPath);
            var protectedSegment = GetLastSegment(entry.ProtectedPath);
            var originalDirectory = physicalChildren.FirstOrDefault(directory =>
                string.Equals(directory.Name, originalSegment, PathComparison));
            var protectedDirectory = physicalChildren.FirstOrDefault(directory =>
                string.Equals(directory.Name, protectedSegment, PathComparison));

            FolderNameEntryState state;
            DirectoryInfo? currentDirectory;
            if (originalDirectory is not null && protectedDirectory is not null &&
                !string.Equals(originalSegment, protectedSegment, PathComparison))
            {
                state = FolderNameEntryState.Conflict;
                currentDirectory = originalDirectory;
                consumed.Add(originalDirectory.FullName);
                consumed.Add(protectedDirectory.FullName);
            }
            else if (protectedDirectory is not null)
            {
                state = FolderNameEntryState.Protected;
                currentDirectory = protectedDirectory;
                consumed.Add(protectedDirectory.FullName);
            }
            else if (originalDirectory is not null)
            {
                state = FolderNameEntryState.Original;
                currentDirectory = originalDirectory;
                consumed.Add(originalDirectory.FullName);
            }
            else
            {
                state = FolderNameEntryState.Stale;
                currentDirectory = null;
                hasMissingMapping = true;
            }

            var node = new FolderNameNode(
                entry,
                state,
                currentDirectory?.FullName,
                IsNew: false,
                GetDepth(entry.OriginalPath));
            nodes.Add(node);
            if (currentDirectory is not null && state != FolderNameEntryState.Conflict)
            {
                AnalyzeChildren(
                    currentDirectory,
                    entry.OriginalPath,
                    entry.ProtectedPath,
                    entries,
                    originalPaths,
                    nodes,
                    mode,
                    includeNewDirectories,
                    cancellationToken);
            }
        }

        if (!includeNewDirectories)
            return;

        var unknown = physicalChildren
            .Where(directory => !consumed.Contains(directory.FullName))
            .ToArray();
        if (hasMissingMapping && unknown.Length > 0)
        {
            foreach (var directory in unknown)
            {
                var conflictEntry = new FolderNameMapEntry
                {
                    OriginalPath = JoinManifestPath(logicalOriginalParent, directory.Name),
                    ProtectedPath = JoinManifestPath(logicalProtectedParent, directory.Name)
                };
                nodes.Add(new FolderNameNode(
                    conflictEntry,
                    FolderNameEntryState.Conflict,
                    directory.FullName,
                    IsNew: true,
                    GetDepth(conflictEntry.OriginalPath)));
            }
            return;
        }

        foreach (var directory in unknown)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var originalPath = JoinManifestPath(logicalOriginalParent, directory.Name);
            if (!originalPaths.Add(originalPath))
                continue;
            var protectedName = CreateProtectedName(mode, originalPath);
            while (physicalChildren.Any(item =>
                       string.Equals(item.Name, protectedName, PathComparison)) ||
                   entries.Any(item =>
                       string.Equals(
                           GetParent(item.ProtectedPath),
                           logicalProtectedParent,
                           PathComparison) &&
                       string.Equals(
                           GetLastSegment(item.ProtectedPath),
                           protectedName,
                           PathComparison)))
            {
                if (mode == FolderNameProtectionMode.Sha256)
                    throw new IOException("A protected folder-name collision was detected.");
                protectedName = CreateProtectedName(mode, originalPath);
            }

            var entry = new FolderNameMapEntry
            {
                OriginalPath = originalPath,
                ProtectedPath = JoinManifestPath(logicalProtectedParent, protectedName)
            };
            entries.Add(entry);
            var node = new FolderNameNode(
                entry,
                FolderNameEntryState.Original,
                directory.FullName,
                IsNew: true,
                GetDepth(originalPath));
            nodes.Add(node);
            AnalyzeChildren(
                directory,
                entry.OriginalPath,
                entry.ProtectedPath,
                entries,
                originalPaths,
                nodes,
                mode,
                includeNewDirectories,
                cancellationToken);
        }
    }

    private static async Task RenameNodesAsync(
        IReadOnlyList<FolderNameNode> nodes,
        bool encrypt,
        IProgress<FolderNameProgress>? progress,
        CancellationToken cancellationToken)
    {
        Preflight(nodes, encrypt);
        for (var index = 0; index < nodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = nodes[index];
            var sourcePath = node.CurrentPath
                ?? throw new DirectoryNotFoundException("A mapped folder no longer exists.");
            var targetName = encrypt
                ? GetLastSegment(node.Entry.ProtectedPath)
                : GetLastSegment(node.Entry.OriginalPath);
            var targetPath = Path.Combine(
                Path.GetDirectoryName(sourcePath)
                ?? throw new InvalidOperationException("The folder has no parent directory."),
                targetName);

            if (Directory.Exists(targetPath) && !PathComparer.Equals(sourcePath, targetPath))
                throw new IOException($"The target folder already exists: {targetPath}");
            if (!PathComparer.Equals(sourcePath, targetPath))
                Directory.Move(sourcePath, targetPath);
            progress?.Report(new FolderNameProgress(targetPath, index + 1, nodes.Count));
            await Task.Yield();
        }
    }

    private static void Preflight(IReadOnlyList<FolderNameNode> nodes, bool encrypt)
    {
        foreach (var node in nodes)
        {
            if (node.CurrentPath is null || !Directory.Exists(node.CurrentPath))
                throw new DirectoryNotFoundException("A mapped folder no longer exists.");
            var targetName = encrypt
                ? GetLastSegment(node.Entry.ProtectedPath)
                : GetLastSegment(node.Entry.OriginalPath);
            if (targetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException($"The target folder name is invalid: {targetName}");
            var target = Path.Combine(Path.GetDirectoryName(node.CurrentPath)!, targetName);
            if (Directory.Exists(target) && !PathComparer.Equals(target, node.CurrentPath))
                throw new IOException($"The target folder already exists: {target}");
        }
    }

    private async Task WriteManifestAsync(
        string rootPath,
        FolderNameManifest manifest,
        byte[] passwordBytes,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        if (json.Length > TextCryptoService.MaximumTextCharacters)
            throw new InvalidDataException("The folder-name manifest is too large.");
        var encrypted = await _textCryptoService.EncryptAsync(
            json,
            passwordBytes,
            cancellationToken).ConfigureAwait(false);
        var manifestPath = GetManifestPath(rootPath);
        var tempPath = Path.Combine(
            rootPath,
            $"{ManifestFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(encrypted);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(manifestPath))
            {
                try
                {
                    File.Replace(tempPath, manifestPath, destinationBackupFileName: null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(tempPath, manifestPath, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, manifestPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static DirectoryInfo ValidateRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Select a folder first.", nameof(rootPath));
        var fullPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Folder not found: {fullPath}");
        var root = new DirectoryInfo(Path.TrimEndingDirectorySeparator(fullPath));
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The selected folder cannot be a symbolic link, junction, or reparse point.");
        if (root.Parent is null)
            throw new IOException("A filesystem root cannot be processed.");
        return root;
    }

    private static void ValidateManifest(FolderNameManifest manifest)
    {
        if (!string.Equals(manifest.Format, ManifestFormat, StringComparison.Ordinal) ||
            manifest.Version != ManifestVersion)
            throw new InvalidDataException("The folder-name manifest format or version is not supported.");
        if (!Enum.IsDefined(manifest.NameMode))
            throw new InvalidDataException("The folder-name manifest contains an invalid naming mode.");
        var originals = new HashSet<string>(PathComparer);
        var protectedPaths = new HashSet<string>(PathComparer);
        foreach (var entry in manifest.Directories)
        {
            ValidateManifestPath(entry.OriginalPath);
            ValidateManifestPath(entry.ProtectedPath);
            if (!originals.Add(entry.OriginalPath) || !protectedPaths.Add(entry.ProtectedPath))
                throw new InvalidDataException("The folder-name manifest contains duplicate paths.");
            if (GetDepth(entry.OriginalPath) != GetDepth(entry.ProtectedPath))
                throw new InvalidDataException("The folder-name manifest contains mismatched path depths.");
        }
    }

    private static void ValidateManifestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.EndsWith('/') ||
            path.Split('/').Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("The folder-name manifest contains an unsafe path.");
    }

    private static bool RebaseRoot(FolderNameManifest manifest, string rootName)
    {
        if (manifest.Directories.Count == 0)
            return false;
        var storedRoot = manifest.Directories
            .Select(entry => entry.OriginalPath.Split('/')[0])
            .Distinct(PathComparer)
            .SingleOrDefault()
            ?? throw new InvalidDataException("The manifest contains inconsistent root folders.");
        if (string.Equals(storedRoot, rootName, PathComparison))
            return false;

        foreach (var entry in manifest.Directories)
        {
            if (!string.Equals(entry.ProtectedPath.Split('/')[0], storedRoot, PathComparison))
                throw new InvalidDataException("The manifest contains inconsistent protected roots.");
            entry.OriginalPath = ReplaceRoot(entry.OriginalPath, rootName);
            entry.ProtectedPath = ReplaceRoot(entry.ProtectedPath, rootName);
        }
        return true;
    }

    private static string ReplaceRoot(string path, string rootName)
    {
        var separator = path.IndexOf('/');
        return separator < 0 ? rootName : rootName + path[separator..];
    }

    private static IEnumerable<DirectoryInfo> EnumerateDirectories(
        DirectoryInfo root,
        CancellationToken cancellationToken)
    {
        foreach (var directory in EnumerateDirectDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return directory;
            foreach (var descendant in EnumerateDirectories(directory, cancellationToken))
                yield return descendant;
        }
    }

    private static IEnumerable<DirectoryInfo> EnumerateDirectDirectories(DirectoryInfo root)
    {
        foreach (var directory in root.EnumerateDirectories())
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
                yield return directory;
        }
    }

    private static void EnsureNoConflicts(FolderNameSession session)
    {
        if (session.ConflictCount > 0)
            throw new IOException("Resolve folder-name conflicts before continuing.");
    }

    private static string CreateProtectedName(FolderNameProtectionMode mode, string originalPath)
    {
        if (mode == FolderNameProtectionMode.Aes)
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(originalPath));
        return Convert.ToHexStringLower(hash);
    }

    private static string GetManifestPath(string rootPath) =>
        Path.Combine(rootPath, ManifestFileName);


    private static string JoinManifestPath(string parent, string child) =>
        $"{parent}/{child}";

    private static string GetParent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? "" : path[..separator];
    }

    private static string GetLastSegment(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static int GetDepth(string path) => path.Count(character => character == '/');

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

internal sealed class FolderNameManifest
{
    public string Format { get; set; } = "";
    public int Version { get; set; }
    public FolderNameProtectionMode NameMode { get; set; }
    public List<FolderNameMapEntry> Directories { get; set; } = [];
}

internal sealed class FolderNameMapEntry
{
    public string OriginalPath { get; set; } = "";
    public string ProtectedPath { get; set; } = "";
}

internal sealed record FolderNameNode(
    FolderNameMapEntry Entry,
    FolderNameEntryState State,
    string? CurrentPath,
    bool IsNew,
    int Depth);
