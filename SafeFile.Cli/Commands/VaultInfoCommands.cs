using System.CommandLine;
using System.Security.Cryptography;
using SafeFile.Cli.Infrastructure;
using SafeFile.Cli.Output;
using SafeFile.Core.IO;

namespace SafeFile.Cli.Commands;

/// <summary><c>info</c> (header, optionally metadata) and <c>verify</c> (password check).</summary>
internal static class VaultInfoCommands
{
    private static readonly Argument<string[]> Sources = new("sources")
    {
        Description = ".safe vaults and/or folders to search recursively for vaults.",
        Arity = ArgumentArity.OneOrMore
    };

    private static readonly Option<string[]> Exclude = new("--exclude")
    {
        Description = "Folder inside a source folder to skip (repeatable).",
        HelpName = "DIR"
    };

    public static Command CreateInfo()
    {
        var command = new Command(
            "info",
            "Show vault details. The original name is shown only when a password option is given.")
        {
            Sources, Exclude
        };
        PasswordOptions.AddTo(command);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, ct, "info",
            ctx => RunAsync(ctx, "info", requirePassword: false)));
        return command;
    }

    public static Command CreateVerify()
    {
        var command = new Command("verify", "Check that a password opens the vaults, without decrypting.")
        {
            Sources, Exclude
        };
        PasswordOptions.AddTo(command);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, ct, "verify",
            ctx => RunAsync(ctx, "verify", requirePassword: true)));
        return command;
    }

    private static async Task<int> RunAsync(CliContext ctx, string operation, bool requirePassword)
    {
        var parse = ctx.Parse;
        var reporter = ctx.Reporter;
        var ct = ctx.CancellationToken;
        var inputs = PathUtil.ResolveExisting(parse.GetValue(Sources)!);
        var excluded = PathUtil.ValidateExclusions(
            parse.GetValue(Exclude), inputs.Where(Directory.Exists).ToArray());
        var vaults = await Task.Run(() => VaultScanner.Scan(inputs, excluded, ct), ct).ConfigureAwait(false);
        if (vaults.Count == 0)
            throw new CliException("no_vaults", "No .safe vaults found.");

        var tracker = new ProgressTracker(reporter, operation, "vaults", operation == "verify" ? "verifying" : "reading")
        {
            Total = vaults.Count,
            TotalItems = vaults.Count
        };
        ctx.Tracker = tracker;
        // info is a quick header read; only verify (one Argon2 derivation per vault) reports progress.
        var showProgress = operation == "verify";
        reporter.Emit("start", Fields.Of(
            ("operation", operation),
            ("schemaVersion", Reporter.SchemaVersion),
            ("totalItems", vaults.Count)));

        var usePassword = requirePassword || PasswordOptions.IsProvided(parse);
        var passwordBytes = usePassword
            ? PasswordOptions.Resolve(parse, ctx.Settings, confirm: false)
            : null;
        int? firstFailureExitCode = null;
        try
        {
            for (var index = 0; index < vaults.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var vault = vaults[index];
                tracker.SetCurrent(vault.Path);
                if (showProgress)
                    tracker.Update(index, index);

                string? originalName = null;
                string? algorithm = null;
                bool? passwordOk = null;
                MappedError? error = vault.Header is null
                    ? new MappedError("invalid_vault", ExitCodes.Failure, vault.HeaderError ?? "Invalid vault header.")
                    : null;

                if (error is null && passwordBytes is not null)
                {
                    try
                    {
                        var metadata = await new FileEncryptor(ctx.Settings.MaxThreads, settings: ctx.Settings)
                            .ReadVaultMetadataAsync(vault.Path, passwordBytes, ct).ConfigureAwait(false);
                        originalName = metadata.OriginalFileName;
                        algorithm = metadata.EncryptionAlgorithm;
                        passwordOk = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        error = ErrorMapper.Map(ex, passwordContext: true);
                        passwordOk = error.Value.Code == "wrong_password" ? false : null;
                    }
                }

                if (error is null)
                    tracker.ItemSucceeded();
                else
                {
                    tracker.ItemFailed();
                    firstFailureExitCode ??= error.Value.ExitCode;
                }

                reporter.Emit("item", Fields.Of(
                    ("operation", operation),
                    ("path", vault.Path),
                    ("status", error is null ? "succeeded" : "failed"),
                    ("sizeBytes", vault.Length),
                    ("lastModifiedUtc", vault.LastWriteTimeUtc.ToString("O")),
                    ("header", vault.Header is null ? null : VaultScanner.DescribeHeader(vault.Header)),
                    ("passwordValid", passwordOk),
                    ("originalName", originalName),
                    ("encryptionAlgorithm", algorithm),
                    ("errorCode", error?.Code),
                    ("error", error?.Message)));
                WriteText(reporter, vault, originalName, algorithm, passwordOk, error);
                if (showProgress)
                    tracker.Update(index + 1, index + 1);
            }
        }
        finally
        {
            if (passwordBytes is not null)
                CryptographicOperations.ZeroMemory(passwordBytes);
        }

        if (showProgress)
            tracker.Flush();
        reporter.EndProgress();
        var exitCode = CommandRunner.ExitCodeFor(tracker.Succeeded, tracker.Failed, firstFailureExitCode);
        reporter.Emit("result", Fields.Of(
            ("operation", operation),
            ("status", CommandRunner.StatusFor(tracker.Succeeded, tracker.Failed)),
            ("exitCode", exitCode),
            ("succeeded", tracker.Succeeded),
            ("failed", tracker.Failed),
            ("totalItems", tracker.TotalItems),
            ("elapsedSeconds", Format.Round(tracker.ElapsedSeconds, 1))));
        if (operation == "verify")
            reporter.Result($"Verified: {tracker.Succeeded} ok, {tracker.Failed} failed.");
        return exitCode;
    }

    private static void WriteText(
        Reporter reporter,
        ScannedVault vault,
        string? originalName,
        string? algorithm,
        bool? passwordOk,
        MappedError? error)
    {
        if (reporter.IsJson)
            return;
        var lines = new List<string> { vault.Path };
        if (vault.Header is { } header)
        {
            lines.Add($"  Mode:        {header.Mode}");
            lines.Add($"  Name mode:   {header.OutputFileNameMode}");
            lines.Add($"  Version:     {header.Version}");
            lines.Add($"  Size:        {Format.Bytes(vault.Length)}");
            lines.Add($"  Chunk size:  {Format.Bytes(header.ChunkSize)}");
            lines.Add($"  KDF:         Argon2id m={header.KdfParameters.MemorySizeKb}KB " +
                      $"t={header.KdfParameters.Iterations} p={header.KdfParameters.Parallelism}");
        }
        if (algorithm is not null)
            lines.Add($"  Cipher:      {algorithm}");
        if (originalName is not null)
            lines.Add($"  Original:    {originalName}");
        if (passwordOk is not null)
            lines.Add($"  Password:    {(passwordOk.Value ? "OK" : "WRONG")}");
        if (error is { } e && passwordOk != false)
            lines.Add($"  Error:       {e.Message}");
        reporter.Result(string.Join(Environment.NewLine, lines));
    }
}
