using System.Security.Cryptography;
using SafeFile.Core.Services;

namespace SafeFile.Cli.Infrastructure;

/// <summary>
/// Process exit codes. These are part of the CLI contract for integrations.
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int InvalidUsage = 2;
    public const int AuthenticationFailed = 3;
    public const int CompletedWithErrors = 4;
    public const int Cancelled = 130;
}

internal sealed class CliException(
    string code,
    string message,
    int exitCode = ExitCodes.Failure) : Exception(message)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
}

internal readonly record struct MappedError(string Code, int ExitCode, string Message);

internal static class ErrorMapper
{
    /// <param name="passwordContext">
    /// True when the failing call authenticates a password (vault metadata,
    /// decryption, folder-name manifest). Core reports a wrong password as
    /// <see cref="InvalidOperationException"/> or <see cref="CryptographicException"/>.
    /// </param>
    public static MappedError Map(Exception ex, bool passwordContext = false) => ex switch
    {
        CliException cli => new(cli.Code, cli.ExitCode, cli.Message),
        OperationCanceledException => new("cancelled", ExitCodes.Cancelled, "Operation cancelled."),
        ExcludedFolderValidationException exclusion => new(
            "invalid_exclusion", ExitCodes.InvalidUsage, DescribeExclusion(exclusion.ResourceKey)),
        CryptographicException or InvalidOperationException when passwordContext => new(
            "wrong_password", ExitCodes.AuthenticationFailed,
            "Wrong password or the data is corrupted."),
        FileNotFoundException or DirectoryNotFoundException => new(
            "not_found", ExitCodes.Failure, ex.Message),
        ArgumentException => new("invalid_argument", ExitCodes.InvalidUsage, ex.Message),
        InvalidDataException => new("invalid_data", ExitCodes.Failure, ex.Message),
        UnauthorizedAccessException => new("access_denied", ExitCodes.Failure, ex.Message),
        IOException => new("io_error", ExitCodes.Failure, ex.Message),
        _ => new("error", ExitCodes.Failure, ex.Message)
    };

    private static string DescribeExclusion(string key) => key switch
    {
        "ExcludeSelectSourceFirst" => "--exclude requires at least one source folder.",
        "ExcludeFolderNotFound" => "An excluded folder does not exist.",
        "ExcludeReparseNotAllowed" => "An excluded folder cannot be a symbolic link, junction, or reparse point.",
        "ExcludeSourceRootNotAllowed" => "A source folder itself cannot be excluded.",
        "ExcludeOutsideSource" => "Every excluded folder must be inside a source folder.",
        _ => "Invalid excluded folder."
    };
}
