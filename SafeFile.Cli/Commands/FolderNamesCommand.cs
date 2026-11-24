using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SafeFile.Cli.Infrastructure;
using SafeFile.Cli.Output;
using SafeFile.Core.Services;

namespace SafeFile.Cli.Commands;

internal static class FolderNamesCommand
{
    private static readonly Argument<string> Root = new("root")
    {
        Description = "Root folder whose sub-folder names are protected (the root itself is never renamed)."
    };

    private static readonly Option<string[]> Exclude = new("--exclude")
    {
        Description = "Sub-folder to leave untouched (repeatable).",
        HelpName = "DIR"
    };

    private static readonly Option<string?> Mode = new Option<string?>("--mode")
    {
        Description = "Name protection for a new manifest: md5 (default), sha256, aes. Ignored when a manifest exists."
    }.AcceptOnlyFromAmong("md5", "sha256", "aes");

    public static Command Create()
    {
        var command = new Command("folder-names", "Protect or restore sub-folder names in place.");

        var status = new Command("status", "Show manifest and folder counts (pass a password to verify the manifest).")
        {
            Root, Exclude
        };
        PasswordOptions.AddTo(status);
        status.SetAction((parse, ct) => CommandRunner.RunAsync(parse, ct, "folder-names.status", StatusAsync));

        var encrypt = new Command("encrypt", "Rename sub-folders to protected names (also protects newly added folders).")
        {
            Root, Exclude, Mode
        };
        PasswordOptions.AddTo(encrypt);
        encrypt.SetAction((parse, ct) => CommandRunner.RunAsync(parse, ct, "folder-names.encrypt",
            ctx => RunAsync(ctx, encrypt: true)));

        var decrypt = new Command("decrypt", "Restore the original sub-folder names.")
        {
            Root
        };
        PasswordOptions.AddTo(decrypt);
        decrypt.SetAction((parse, ct) => CommandRunner.RunAsync(parse, ct, "folder-names.decrypt",
            ctx => RunAsync(ctx, encrypt: false)));

        command.Add(status);
        command.Add(encrypt);
        command.Add(decrypt);
        return command;
    }

    private static FolderNameProtectionService CreateService() => new(
        new TextCryptoService(), NullLogger<FolderNameProtectionService>.Instance);

    private static (string Root, IReadOnlyList<string> Excluded) ResolveRoot(ParseResult parse, bool allowExclusions)
    {
        var root = PathUtil.Normalize(parse.GetValue(Root)!);
        if (!Directory.Exists(root))
            throw new CliException("not_found", $"Folder not found: {root}");
        var excluded = allowExclusions
            ? PathUtil.ValidateExclusions(parse.GetValue(Exclude), [root])
            : [];
        return (root, excluded);
    }

    private static async Task<int> StatusAsync(CliContext ctx)
    {
        const string operation = "folder-names.status";
        var (root, excluded) = ResolveRoot(ctx.Parse, allowExclusions: true);
        var service = CreateService();
        var scan = await service.ScanAsync(root, ctx.CancellationToken, excluded).ConfigureAwait(false);

        FolderNameSession? session = null;
        if (scan.HasManifest && PasswordOptions.IsProvided(ctx.Parse))
        {
            var passwordBytes = PasswordOptions.Resolve(ctx.Parse, ctx.Settings, confirm: false);
            try
            {
                session = await VerifyAsync(service, root, passwordBytes, excluded, ctx.CancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }

        ctx.Reporter.Emit("result", Fields.Of(
            ("operation", operation),
            ("status", "succeeded"),
            ("exitCode", ExitCodes.Success),
            ("root", scan.RootPath),
            ("hasManifest", scan.HasManifest),
            ("manifestVerified", session is not null),
            ("physicalFolderCount", scan.PhysicalFolderCount),
            ("mode", session?.Mode.ToString().ToLowerInvariant()),
            ("protectedCount", session?.ProtectedCount),
            ("clearCount", session?.ClearCount),
            ("newCount", session?.NewCount),
            ("conflictCount", session?.ConflictCount)));

        var lines = new List<string>
        {
            scan.RootPath,
            $"  Manifest:    {(scan.HasManifest ? session is null ? "present (not verified)" : "present, verified" : "none")}",
            $"  Folders:     {scan.PhysicalFolderCount:N0}"
        };
        if (session is not null)
        {
            lines.Add($"  Mode:        {session.Mode}");
            lines.Add($"  Protected:   {session.ProtectedCount:N0}");
            lines.Add($"  Clear:       {session.ClearCount:N0} ({session.NewCount:N0} new)");
            lines.Add($"  Conflicts:   {session.ConflictCount:N0}");
        }
        ctx.Reporter.Result(string.Join(Environment.NewLine, lines));
        return ExitCodes.Success;
    }

    private static async Task<int> RunAsync(CliContext ctx, bool encrypt)
    {
        var operation = encrypt ? "folder-names.encrypt" : "folder-names.decrypt";
        var reporter = ctx.Reporter;
        var ct = ctx.CancellationToken;
        var (root, excluded) = ResolveRoot(ctx.Parse, allowExclusions: encrypt);
        var service = CreateService();
        var scan = await service.ScanAsync(root, ct, excluded).ConfigureAwait(false);
        if (!encrypt && !scan.HasManifest)
            throw new CliException("no_manifest", $"No folder-name manifest found in {root}.");

        var passwordBytes = PasswordOptions.Resolve(ctx.Parse, ctx.Settings, confirm: encrypt && !scan.HasManifest);
        try
        {
            FolderNameSession session;
            if (scan.HasManifest)
            {
                session = await VerifyAsync(service, root, passwordBytes, excluded, ct).ConfigureAwait(false);
                var requested = ctx.Parse.GetValue(Mode);
                if (encrypt && requested is not null &&
                    !string.Equals(requested, session.Mode.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    var warning = $"A manifest already exists; keeping its mode ({session.Mode}) instead of {requested}.";
                    reporter.Emit("warning", Fields.Of(("operation", operation), ("code", "mode_ignored"), ("message", warning)));
                    reporter.Message("Warning: " + warning);
                }
            }
            else
            {
                if (Encoding.UTF8.GetCharCount(passwordBytes) < ctx.Settings.MinPasswordLength)
                    throw new CliException(
                        "password_too_short",
                        $"Password must be at least {ctx.Settings.MinPasswordLength} characters.",
                        ExitCodes.InvalidUsage);
                var mode = Enum.Parse<FolderNameProtectionMode>(ctx.Parse.GetValue(Mode) ?? "md5", ignoreCase: true);
                session = await service.CreateSessionAsync(root, mode, ct, excluded).ConfigureAwait(false);
            }

            var pending = encrypt ? session.ClearCount : session.ProtectedCount;
            var tracker = new ProgressTracker(reporter, operation, "folders", encrypt ? "encrypting" : "decrypting")
            {
                Total = pending,
                TotalItems = pending
            };
            ctx.Tracker = tracker;
            reporter.Emit("start", Fields.Of(
                ("operation", operation),
                ("schemaVersion", Reporter.SchemaVersion),
                ("root", root),
                ("mode", session.Mode.ToString().ToLowerInvariant()),
                ("totalItems", pending),
                ("protectedCount", session.ProtectedCount),
                ("clearCount", session.ClearCount),
                ("conflictCount", session.ConflictCount)));
            reporter.Message($"{(encrypt ? "Protecting" : "Restoring")} {pending:N0} folder name(s) in {root} ({session.Mode})");

            var completed = 0;
            var progress = new SyncProgress<FolderNameProgress>(tracker.Gate, p =>
            {
                tracker.Total = p.Total;
                tracker.TotalItems = p.Total;
                tracker.SetCurrent(p.CurrentPath);
                for (; completed < p.Completed; completed++)
                    tracker.ItemSucceeded();
                tracker.Update(p.Completed, p.Completed);
            });

            if (encrypt)
                await service.EncryptAsync(session, passwordBytes, progress, ct).ConfigureAwait(false);
            else
                await service.DecryptAsync(session, passwordBytes, progress, ct).ConfigureAwait(false);

            tracker.Flush();
            reporter.EndProgress();
            reporter.Emit("result", Fields.Of(
                ("operation", operation),
                ("status", "succeeded"),
                ("exitCode", ExitCodes.Success),
                ("root", root),
                ("mode", session.Mode.ToString().ToLowerInvariant()),
                ("renamed", completed),
                ("succeeded", tracker.Succeeded),
                ("failed", 0),
                ("elapsedSeconds", Format.Round(tracker.ElapsedSeconds, 1))));
            reporter.Result(
                $"{(encrypt ? "Protected" : "Restored")} {completed:N0} folder name(s) in {Format.Duration(tracker.ElapsedSeconds)}.");
            return ExitCodes.Success;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static async Task<FolderNameSession> VerifyAsync(
        FolderNameProtectionService service,
        string root,
        byte[] passwordBytes,
        IReadOnlyCollection<string> excluded,
        CancellationToken ct)
    {
        try
        {
            return await service.VerifyManifestAsync(root, passwordBytes, ct, excluded).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        {
            var error = ErrorMapper.Map(ex, passwordContext: true);
            throw new CliException(error.Code, error.Message, error.ExitCode);
        }
    }
}
