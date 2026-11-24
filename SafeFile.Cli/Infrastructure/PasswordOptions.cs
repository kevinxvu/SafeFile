using System.CommandLine;
using System.Text;
using SafeFile.Core.Models;

namespace SafeFile.Cli.Infrastructure;

/// <summary>
/// Password sources. Exactly one may be given; with none, the CLI prompts
/// (hidden input) when a terminal is attached.
/// </summary>
internal static class PasswordOptions
{
    public static readonly Option<string?> Password = new("--password", "-p")
    {
        Description = "Password given inline. Convenient, but visible in shell history and process lists."
    };

    public static readonly Option<bool> PasswordStdin = new("--password-stdin")
    {
        Description = "Read the password from the first line of standard input."
    };

    public static readonly Option<string?> PasswordEnv = new("--password-env")
    {
        Description = "Read the password from the named environment variable.",
        HelpName = "VARIABLE"
    };

    public static readonly Option<FileInfo?> PasswordFile = new("--password-file")
    {
        Description = "Read the password from a file (a single trailing newline is ignored).",
        HelpName = "PATH"
    };

    public static void AddTo(Command command)
    {
        command.Add(Password);
        command.Add(PasswordStdin);
        command.Add(PasswordEnv);
        command.Add(PasswordFile);
    }

    public static bool IsProvided(ParseResult parse) =>
        parse.GetValue(Password) is not null ||
        parse.GetValue(PasswordStdin) ||
        parse.GetValue(PasswordEnv) is not null ||
        parse.GetValue(PasswordFile) is not null;

    /// <summary>Returns UTF-8 password bytes. Callers must zero them when done.</summary>
    /// <param name="confirm">Ask twice when prompting interactively (new passwords).</param>
    public static byte[] Resolve(ParseResult parse, AppSettings settings, bool confirm)
    {
        var sources = new[]
        {
            parse.GetValue(Password) is not null,
            parse.GetValue(PasswordStdin),
            parse.GetValue(PasswordEnv) is not null,
            parse.GetValue(PasswordFile) is not null
        }.Count(provided => provided);
        if (sources > 1)
            throw new CliException(
                "invalid_argument",
                "Use only one of --password, --password-stdin, --password-env, --password-file.",
                ExitCodes.InvalidUsage);

        string? password;
        if (parse.GetValue(Password) is { } inline)
        {
            password = inline;
        }
        else if (parse.GetValue(PasswordStdin))
        {
            password = Console.In.ReadLine();
        }
        else if (parse.GetValue(PasswordEnv) is { } variable)
        {
            password = Environment.GetEnvironmentVariable(variable)
                ?? throw new CliException(
                    "password_required",
                    $"Environment variable '{variable}' is not set.",
                    ExitCodes.InvalidUsage);
        }
        else if (parse.GetValue(PasswordFile) is { } file)
        {
            if (!file.Exists)
                throw new CliException("not_found", $"Password file not found: {file.FullName}");
            password = TrimSingleNewline(File.ReadAllText(file.FullName));
        }
        else
        {
            password = Prompt(confirm && settings.ConfirmPasswordToggle);
        }

        if (string.IsNullOrEmpty(password))
            throw new CliException("password_required", "Password cannot be empty.", ExitCodes.InvalidUsage);
        return Encoding.UTF8.GetBytes(password);
    }

    private static string Prompt(bool confirm)
    {
        if (Console.IsInputRedirected)
            throw new CliException(
                "password_required",
                "A password is required. Use --password, --password-stdin, --password-env or --password-file.",
                ExitCodes.InvalidUsage);

        var password = ReadHidden("Password: ");
        if (confirm && ReadHidden("Confirm password: ") != password)
            throw new CliException("password_mismatch", "Passwords do not match.", ExitCodes.InvalidUsage);
        return password;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Error.Write(prompt);
        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                    builder.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                builder.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return builder.ToString();
    }

    private static string TrimSingleNewline(string value) =>
        value.EndsWith("\r\n", StringComparison.Ordinal) ? value[..^2]
        : value.EndsWith('\n') ? value[..^1]
        : value;
}
