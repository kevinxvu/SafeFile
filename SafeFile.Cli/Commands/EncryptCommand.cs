using System.CommandLine;
using System.Security.Cryptography;
using SafeFile.Cli.Infrastructure;
using SafeFile.Cli.Output;
using SafeFile.Core.Crypto;
using SafeFile.Core.Format;
using SafeFile.Core.IO;
using SafeFile.Core.Models;

namespace SafeFile.Cli.Commands;

internal static class EncryptCommand
{
    private const string Operation = "encrypt";

    private static readonly Argument<string[]> Sources = new("sources")
    {
        Description = "Files and/or folders to encrypt.",
        Arity = ArgumentArity.OneOrMore
    };

    private static readonly Option<string?> Output = new("--output", "-o")
    {
        Description = "Output folder (default: current directory).",
        HelpName = "DIR"
    };

    private static readonly Option<string> Mode = new Option<string>("--mode")
    {
        Description = "Folder mode: zip = one vault per folder; perfile = one vault per file in '<folder>_encrypted'.",
        DefaultValueFactory = _ => "zip"
    }.AcceptOnlyFromAmong("zip", "perfile");

    private static readonly Option<string> NameMode = new Option<string>("--name-mode")
    {
        Description = "Protect output file names.",
        DefaultValueFactory = _ => "none"
    }.AcceptOnlyFromAmong("none", "aes", "sha256", "md5");

    private static readonly Option<string[]> Exclude = new("--exclude")
    {
        Description = "Folder inside a source folder to skip (repeatable).",
        HelpName = "DIR"
    };

    private static readonly Option<bool> Overwrite = new("--overwrite")
    {
        Description = "Overwrite existing output files."
    };

    private static readonly Option<int?> ChunkSizeMb = new("--chunk-size-mb")
    {
        Description = "Chunk size in MB, 1-16 (default: the app setting)."
    };

    private static readonly Option<int?> Argon2MemoryKb = new("--argon2-memory-kb")
    {
        Description = "Argon2id memory in KB (default: the app setting)."
    };

    private static readonly Option<int?> Argon2Iterations = new("--argon2-iterations")
    {
        Description = "Argon2id iterations (default: the app setting)."
    };

    private static readonly Option<int?> Argon2Parallelism = new("--argon2-parallelism")
    {
        Description = "Argon2id parallelism (default: the app setting)."
    };

    public static Command Create()
    {
        var command = new Command("encrypt", "Encrypt files and folders into .safe vaults.")
        {
            Sources, Output, Mode, NameMode, Exclude, Overwrite,
            ChunkSizeMb, Argon2MemoryKb, Argon2Iterations, Argon2Parallelism
        };
        PasswordOptions.AddTo(command);
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, ct, Operation, RunAsync));
        return command;
    }

    private sealed record Job(
        string Kind,
        string Source,
        string Destination,
        long Size,
        int FileCount,
        IReadOnlyCollection<string> Excluded);

    private static async Task<int> RunAsync(CliContext ctx)
    {
        var parse = ctx.Parse;
        var reporter = ctx.Reporter;
        var settings = ctx.Settings;
        var ct = ctx.CancellationToken;
        ApplyOverrides(parse, settings);

        var perFileFolders = parse.GetValue(Mode) == "perfile";
        var nameMode = Enum.Parse<OutputFileNameMode>(parse.GetValue(NameMode)!, ignoreCase: true);
        var overwrite = parse.GetValue(Overwrite);
        var outputDirectory = PathUtil.Normalize(parse.GetValue(Output) ?? Environment.CurrentDirectory);
        var sources = PathUtil.ResolveExisting(parse.GetValue(Sources)!);
        var excluded = PathUtil.ValidateExclusions(
            parse.GetValue(Exclude), sources.Where(Directory.Exists).ToArray());

        var jobs = await Task.Run(
            () => sources.Select(source => BuildJob(source, outputDirectory, perFileFolders, excluded, ct)).ToList(),
            ct).ConfigureAwait(false);

        var tracker = new ProgressTracker(reporter, Operation, "bytes", "encrypting")
        {
            Total = jobs.Sum(job => job.Size),
            TotalItems = jobs.Sum(job => job.FileCount)
        };
        ctx.Tracker = tracker;

        reporter.Emit("start", Fields.Of(
            ("operation", Operation),
            ("schemaVersion", Reporter.SchemaVersion),
            ("outputDirectory", outputDirectory),
            ("totalBytes", tracker.Total),
            ("totalItems", tracker.TotalItems),
            ("nameMode", nameMode.ToString().ToLowerInvariant()),
            ("sources", jobs.Select(job => new
            {
                path = job.Source,
                kind = job.Kind,
                sizeBytes = job.Size,
                fileCount = job.FileCount,
                destination = job.Destination
            }).ToArray())));

        var passwordBytes = PasswordOptions.Resolve(parse, settings, confirm: true);
        reporter.Message(
            $"Encrypting {jobs.Count} source(s), {Format.Bytes(tracker.Total)} -> {outputDirectory}");
        Directory.CreateDirectory(outputDirectory);

        var outputs = new List<string>();
        int? firstFailureExitCode = null;
        long completedBytes = 0;
        try
        {
            for (var index = 0; index < jobs.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var job = jobs[index];
                tracker.SetCurrent(job.Source, "encrypting");
                reporter.Emit("item-start", Fields.Of(
                    ("operation", Operation),
                    ("index", index),
                    ("path", job.Source),
                    ("kind", job.Kind),
                    ("sizeBytes", job.Size),
                    ("destination", job.Destination)));

                var failureExitCode = job.Kind == "perfile"
                    ? await EncryptPerFileAsync(job, completedBytes, passwordBytes, nameMode, overwrite, ctx, tracker, outputs)
                        .ConfigureAwait(false)
                    : await EncryptSingleAsync(job, completedBytes, passwordBytes, nameMode, overwrite, ctx, tracker, outputs)
                        .ConfigureAwait(false);
                firstFailureExitCode ??= failureExitCode;
                completedBytes += job.Size;
                tracker.Update(completedBytes, completedBytes, includeSample: false, force: true);
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
            ("outputDirectory", outputDirectory),
            ("outputs", outputs)));
        reporter.Result(
            $"Encryption finished: {tracker.Succeeded} succeeded, {tracker.Failed} failed " +
            $"in {Format.Duration(tracker.ElapsedSeconds)}. Output: {outputDirectory}");
        return exitCode;
    }

    private static async Task<int?> EncryptSingleAsync(
        Job job,
        long completedBytes,
        byte[] passwordBytes,
        OutputFileNameMode nameMode,
        bool overwrite,
        CliContext ctx,
        ProgressTracker tracker,
        List<string> outputs)
    {
        var settings = ctx.Settings;
        var observed = 0d;
        var progress = new SyncProgress<double>(tracker.Gate, value =>
        {
            observed = Math.Max(observed, Math.Clamp(value, 0, 1));
            if (job.Kind == "zip")
                tracker.SetPhase(observed is >= 0.99 and < 1 ? "finalizing" : "encrypting");
            var done = completedBytes + (long)(job.Size * observed);
            tracker.Update(done, done);
        });
        var encryptor = new FileEncryptor(settings.MaxThreads, progress, settings);
        try
        {
            var output = job.Kind == "file"
                ? await encryptor.EncryptFileAsync(
                    job.Source, job.Destination, passwordBytes,
                    settings.GetChunkSizeBytes(), settings.GetKdfParameters(),
                    nameMode, ctx.CancellationToken, overwrite).ConfigureAwait(false)
                : await encryptor.EncryptFolderZipAsync(
                    job.Source, job.Destination, passwordBytes,
                    settings.GetChunkSizeBytes(), settings.GetKdfParameters(),
                    nameMode, ctx.CancellationToken, overwrite, job.Excluded).ConfigureAwait(false);
            tracker.ItemSucceeded();
            lock (tracker.Gate)
                outputs.Add(output);
            ReportItem(ctx.Reporter, job.Source, job.Kind, null, "succeeded", output, null);
            ctx.Reporter.Message($"OK    {job.Source} -> {output}");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = ErrorMapper.Map(ex);
            tracker.ItemFailed();
            ReportItem(ctx.Reporter, job.Source, job.Kind, null, "failed", null, error);
            ctx.Reporter.Message($"FAIL  {job.Source}: {error.Message}");
            return error.ExitCode;
        }
    }

    private static async Task<int?> EncryptPerFileAsync(
        Job job,
        long completedBytes,
        byte[] passwordBytes,
        OutputFileNameMode nameMode,
        bool overwrite,
        CliContext ctx,
        ProgressTracker tracker,
        List<string> outputs)
    {
        var settings = ctx.Settings;
        long transferred = 0, resolved = 0;
        int succeeded = 0, failed = 0;
        var observedByPath = new Dictionary<string, double>(PathUtil.PathComparer);
        var completedPaths = new HashSet<string>(PathUtil.PathComparer);

        var perFileProgress = new SyncProgress<PerFileProgress>(tracker.Gate, p =>
        {
            var size = PathUtil.TryGetLength(p.SourceFilePath);
            tracker.SetCurrent(p.SourceFilePath);
            if (p.Result == PerFileResult.InProgress)
                observedByPath[p.SourceFilePath] = Math.Max(
                    observedByPath.GetValueOrDefault(p.SourceFilePath), Math.Clamp(p.Progress, 0, 1));
            var observed = observedByPath.GetValueOrDefault(p.SourceFilePath);

            if (p.Result != PerFileResult.InProgress && completedPaths.Add(p.SourceFilePath))
            {
                resolved += size;
                if (p.Result == PerFileResult.Succeeded)
                {
                    transferred += size;
                    succeeded++;
                    tracker.ItemSucceeded();
                    ReportItem(ctx.Reporter, p.SourceFilePath, "file", job.Source, "succeeded", null, null);
                }
                else
                {
                    transferred += (long)(size * observed);
                    failed++;
                    tracker.ItemFailed();
                    var error = p.Result == PerFileResult.DestinationExists
                        ? new MappedError("destination_exists", ExitCodes.Failure, "Output file already exists.")
                        : new MappedError("error", ExitCodes.Failure, "Encryption failed (see log for details).");
                    ReportItem(ctx.Reporter, p.SourceFilePath, "file", job.Source, "failed", null, error);
                    ctx.Reporter.Message($"FAIL  {p.SourceFilePath}: {error.Message}");
                }
            }

            var current = p.Result == PerFileResult.InProgress ? (long)(size * observed) : 0;
            tracker.Update(
                completedBytes + transferred + current,
                completedBytes + resolved + current,
                includeSample: p.Result != PerFileResult.DestinationExists);
        });

        var encryptor = new FileEncryptor(settings.MaxThreads, null, settings, perFileProgress);
        MappedError? folderError = null;
        try
        {
            await encryptor.EncryptFolderPerFileAsync(
                job.Source, job.Destination, passwordBytes,
                settings.GetChunkSizeBytes(), settings.GetKdfParameters(),
                nameMode, ctx.CancellationToken, overwrite, job.Excluded).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException) when (failed > 0)
        {
            // Core throws once at the end when any file failed; each failure was already reported.
        }
        catch (Exception ex)
        {
            folderError = ErrorMapper.Map(ex);
            lock (tracker.Gate)
            {
                var unresolved = Math.Max(1, job.FileCount - succeeded - failed);
                for (var i = 0; i < unresolved; i++)
                    tracker.ItemFailed();
                failed += unresolved;
            }
            ctx.Reporter.Message($"FAIL  {job.Source}: {folderError.Value.Message}");
        }

        if (succeeded > 0)
        {
            lock (tracker.Gate)
                outputs.Add(job.Destination);
        }
        var status = CommandRunner.StatusFor(succeeded, failed);
        ctx.Reporter.Emit("item", Fields.Of(
            ("operation", Operation),
            ("path", job.Source),
            ("kind", job.Kind),
            ("parent", null),
            ("status", status),
            ("output", succeeded > 0 ? job.Destination : null),
            ("succeeded", succeeded),
            ("failed", failed),
            ("errorCode", folderError?.Code),
            ("error", folderError?.Message)));
        if (folderError is null)
            ctx.Reporter.Message($"{(failed == 0 ? "OK  " : "PART")}  {job.Source} -> {job.Destination} ({succeeded} ok, {failed} failed)");
        return failed == 0 ? null : folderError?.ExitCode ?? ExitCodes.Failure;
    }

    private static void ReportItem(
        Reporter reporter,
        string path,
        string kind,
        string? parent,
        string status,
        string? output,
        MappedError? error) =>
        reporter.Emit("item", Fields.Of(
            ("operation", Operation),
            ("path", path),
            ("kind", kind),
            ("parent", parent),
            ("status", status),
            ("output", output),
            ("errorCode", error?.Code),
            ("error", error?.Message)));

    private static Job BuildJob(
        string source,
        string outputDirectory,
        bool perFileFolders,
        IReadOnlyList<string> excluded,
        CancellationToken ct)
    {
        if (File.Exists(source))
        {
            return new Job(
                "file", source,
                Path.Combine(outputDirectory, Path.GetFileName(source) + ".safe"),
                new FileInfo(source).Length, 1, []);
        }

        var folderExcluded = excluded.Where(path => PathUtil.IsUnder(source, path)).ToArray();
        var files = PathUtil.EnumerateRegularFiles(new DirectoryInfo(source), folderExcluded, ct).ToList();
        var size = files.Sum(file => file.Length);
        var name = PathUtil.DisplayName(source);
        return perFileFolders
            ? new Job("perfile", source, Path.Combine(outputDirectory, name + "_encrypted"),
                size, files.Count, folderExcluded)
            : new Job("zip", source, Path.Combine(outputDirectory, name + ".safe"),
                size, 1, folderExcluded);
    }

    private static void ApplyOverrides(ParseResult parse, AppSettings settings)
    {
        if (parse.GetValue(ChunkSizeMb) is { } chunk)
        {
            if (chunk is < 1 or > 16)
                throw new CliException("invalid_argument", "--chunk-size-mb must be between 1 and 16.", ExitCodes.InvalidUsage);
            settings.DefaultChunkSizeMb = chunk;
        }

        var kdf = new Argon2Parameters(
            parse.GetValue(Argon2MemoryKb) ?? settings.Argon2MemorySizeKb,
            parse.GetValue(Argon2Iterations) ?? settings.Argon2Iterations,
            parse.GetValue(Argon2Parallelism) ?? settings.Argon2Parallelism);
        try
        {
            kdf.Validate();
        }
        catch (Exception ex)
        {
            throw new CliException("invalid_argument", ex.Message, ExitCodes.InvalidUsage);
        }
        settings.SetKdfParameters(kdf);
    }
}
