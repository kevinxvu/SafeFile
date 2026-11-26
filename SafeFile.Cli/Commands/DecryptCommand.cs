using System.CommandLine;
using System.Security.Cryptography;
using SafeFile.Cli.Infrastructure;
using SafeFile.Cli.Output;
using SafeFile.Core.Format;
using SafeFile.Core.IO;

namespace SafeFile.Cli.Commands;

internal static class DecryptCommand
{
    private const string Operation = "decrypt";

    private static readonly Argument<string[]> Sources = new("sources")
    {
        Description = ".safe vaults and/or folders to search recursively for vaults.",
        Arity = ArgumentArity.OneOrMore
    };

    private static readonly Option<string?> Output = new("--output", "-o")
    {
        Description = "Output folder (default: current directory). Folder layout is preserved.",
        HelpName = "DIR"
    };

    private static readonly Option<string[]> Exclude = new("--exclude")
    {
        Description = "Folder inside a source folder to skip (repeatable).",
        HelpName = "DIR"
    };

    private static readonly Option<bool> Overwrite = new("--overwrite")
    {
        Description = "Overwrite existing output files (folders from zip vaults are never overwritten)."
    };

    private static readonly Option<bool> DeleteIncomplete = new("--delete-incomplete")
    {
        Description = "When a vault fails or is cancelled midway, delete its partial output " +
            "(default: keep the partial file or partly extracted folder)."
    };

    public static Command Create()
    {
        var command = new Command("decrypt", "Decrypt .safe vaults.")
        {
            Sources, Output, Exclude, Overwrite, DeleteIncomplete
        };
        PasswordOptions.AddTo(command);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, ct, Operation, RunAsync));
        return command;
    }

    private static async Task<int> RunAsync(CliContext ctx)
    {
        var parse = ctx.Parse;
        var reporter = ctx.Reporter;
        var settings = ctx.Settings;
        var ct = ctx.CancellationToken;
        var overwrite = parse.GetValue(Overwrite);
        var outputRoot = PathUtil.Normalize(parse.GetValue(Output) ?? Environment.CurrentDirectory);
        var inputs = PathUtil.ResolveExisting(parse.GetValue(Sources)!);
        var excluded = PathUtil.ValidateExclusions(
            parse.GetValue(Exclude), inputs.Where(Directory.Exists).ToArray());

        var vaults = await Task.Run(() => VaultScanner.Scan(inputs, excluded, ct), ct).ConfigureAwait(false);
        var tracker = new ProgressTracker(reporter, Operation, "bytes", "decrypting")
        {
            Total = vaults.Sum(vault => vault.Length),
            TotalItems = vaults.Count
        };
        ctx.Tracker = tracker;

        reporter.Emit("start", Fields.Of(
            ("operation", Operation),
            ("schemaVersion", Reporter.SchemaVersion),
            ("outputDirectory", outputRoot),
            ("totalBytes", tracker.Total),
            ("totalItems", tracker.TotalItems),
            ("vaults", vaults.Select(vault => new
            {
                path = vault.Path,
                sizeBytes = vault.Length,
                relativeDirectory = vault.RelativeDirectory,
                mode = vault.Header?.Mode.ToString().ToLowerInvariant(),
                valid = vault.Header is not null
            }).ToArray())));

        if (vaults.Count == 0)
            throw new CliException("no_vaults", "No .safe vaults found.");

        var passwordBytes = PasswordOptions.Resolve(parse, settings, confirm: false);
        reporter.Message($"Decrypting {vaults.Count} vault(s), {Format.Bytes(tracker.Total)} -> {outputRoot}");
        Directory.CreateDirectory(outputRoot);

        var outputs = new List<string>();
        int? firstFailureExitCode = null;
        long transferred = 0, resolved = 0;
        try
        {
            for (var index = 0; index < vaults.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var vault = vaults[index];
                tracker.SetCurrent(vault.Path, "decrypting");
                reporter.Emit("item-start", Fields.Of(
                    ("operation", Operation),
                    ("index", index),
                    ("path", vault.Path),
                    ("sizeBytes", vault.Length)));

                var observed = 0d;
                var succeeded = false;
                try
                {
                    if (vault.Header is null)
                        throw new CliException("invalid_vault", vault.HeaderError ?? "Invalid vault header.");

                    var output = await DecryptVaultAsync(vault, outputRoot, passwordBytes, overwrite, ctx,
                        new SyncProgress<double>(tracker.Gate, value =>
                        {
                            observed = Math.Max(observed, Math.Clamp(value, 0, 1));
                            var current = (long)(vault.Length * observed);
                            tracker.Update(transferred + current, resolved + current);
                        })).ConfigureAwait(false);

                    succeeded = true;
                    tracker.ItemSucceeded();
                    outputs.Add(output);
                    ReportItem(reporter, vault, "succeeded", output, null);
                    reporter.Message($"OK    {vault.Path} -> {output}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var error = ErrorMapper.Map(ex, passwordContext: true);
                    tracker.ItemFailed();
                    firstFailureExitCode ??= error.ExitCode;
                    ReportItem(reporter, vault, "failed", null, error);
                    reporter.Message($"FAIL  {vault.Path}: {error.Message}");
                }
                finally
                {
                    transferred += succeeded ? vault.Length : (long)(vault.Length * observed);
                    resolved += vault.Length;
                    tracker.Update(transferred, resolved, includeSample: succeeded || observed > 0, force: true);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }

        tracker.Flush();
        reporter.EndProgress();
        var exitCode = CommandRunner.ExitCodeFor(tracker.Succeeded, tracker.Failed, firstFailureExitCode);
        reporter.Emit("result", Fields.Of(
            ("operation", Operation),
            ("status", CommandRunner.StatusFor(tracker.Succeeded, tracker.Failed)),
            ("exitCode", exitCode),
            ("succeeded", tracker.Succeeded),
            ("failed", tracker.Failed),
            ("totalItems", tracker.TotalItems),
            ("totalBytes", tracker.Total),
            ("elapsedSeconds", Format.Round(tracker.ElapsedSeconds, 1)),
            ("outputDirectory", outputRoot),
            ("outputs", outputs)));
        reporter.Result(
            $"Decryption finished: {tracker.Succeeded} succeeded, {tracker.Failed} failed " +
            $"in {Format.Duration(tracker.ElapsedSeconds)}. Output: {outputRoot}");
        return exitCode;
    }

    /// <summary>Same destination and overwrite rules as the desktop Decrypt page.</summary>
    private static async Task<string> DecryptVaultAsync(
        ScannedVault vault,
        string outputRoot,
        byte[] passwordBytes,
        bool overwrite,
        CliContext ctx,
        IProgress<double> progress)
    {
        var settings = ctx.Settings;
        var ct = ctx.CancellationToken;
        var metadata = await new FileEncryptor(settings.MaxThreads, settings: settings)
            .ReadVaultMetadataAsync(vault.Path, passwordBytes, ct).ConfigureAwait(false);

        var destinationRoot = string.IsNullOrEmpty(vault.RelativeDirectory)
            ? outputRoot
            : Path.Combine(outputRoot, vault.RelativeDirectory);
        Directory.CreateDirectory(destinationRoot);

        var mode = vault.Header!.Mode;
        var destination = mode == VaultMode.Zip
            ? Path.Combine(destinationRoot, Path.GetFileNameWithoutExtension(metadata.OriginalFileName))
            : Path.Combine(destinationRoot, metadata.OriginalFileName);
        if ((File.Exists(destination) || Directory.Exists(destination)) &&
            !(mode is VaultMode.File or VaultMode.PerFile && overwrite && File.Exists(destination)))
        {
            throw new CliException("destination_exists", $"Output already exists: {destination}");
        }

        var encryptor = new FileEncryptor(settings.MaxThreads, progress, settings)
        {
            DeleteIncompleteDecryptionOutput = ctx.Parse.GetValue(DeleteIncomplete)
        };
        switch (mode)
        {
            case VaultMode.File:
                return await encryptor.DecryptFileAsync(vault.Path, destination, passwordBytes, ct, overwrite)
                    .ConfigureAwait(false);
            case VaultMode.Zip:
                await encryptor.DecryptFolderZipAsync(vault.Path, destination, passwordBytes, ct)
                    .ConfigureAwait(false);
                return destination;
            case VaultMode.PerFile:
                return await encryptor.DecryptPerFileVaultAsync(vault.Path, destination, passwordBytes, ct, overwrite)
                    .ConfigureAwait(false);
            default:
                throw new CliException("invalid_vault", $"Unsupported vault mode: {mode}");
        }
    }

    private static void ReportItem(
        Reporter reporter,
        ScannedVault vault,
        string status,
        string? output,
        MappedError? error) =>
        reporter.Emit("item", Fields.Of(
            ("operation", Operation),
            ("path", vault.Path),
            ("kind", vault.Header?.Mode.ToString().ToLowerInvariant()),
            ("parent", null),
            ("status", status),
            ("output", output),
            ("errorCode", error?.Code),
            ("error", error?.Message)));
}
