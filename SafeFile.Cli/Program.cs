using System.CommandLine;
using System.Text;
using SafeFile.Cli.Commands;
using SafeFile.Cli.Infrastructure;

namespace SafeFile.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // Some hosts do not allow changing the encoding; JSON output uses its own UTF-8 writer.
        }

        var root = new RootCommand(
            "SafeFile CLI - encrypt and decrypt files and folders (AES-256-GCM, Argon2id).")
        {
            EncryptCommand.Create(),
            DecryptCommand.Create(),
            VaultInfoCommands.CreateVerify(),
            VaultInfoCommands.CreateInfo(),
            FolderNamesCommand.Create(),
            GenPassCommand.Create()
        };
        GlobalOptions.AddTo(root);

        var parse = root.Parse(args);
        var configuration = new InvocationConfiguration
        {
            // Give Core time to delete partial output after Ctrl+C.
            ProcessTerminationTimeout = TimeSpan.FromSeconds(30)
        };
        var exitCode = await parse.InvokeAsync(configuration).ConfigureAwait(false);
        return parse.Errors.Count > 0 && exitCode != ExitCodes.Success
            ? ExitCodes.InvalidUsage
            : exitCode;
    }
}
