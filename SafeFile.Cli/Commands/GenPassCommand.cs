using System.CommandLine;
using SafeFile.Cli.Infrastructure;
using SafeFile.Cli.Output;
using SafeFile.Core.Services;

namespace SafeFile.Cli.Commands;

internal static class GenPassCommand
{
    private static readonly Option<int> Length = new("--length", "-l")
    {
        Description = $"Password length ({PasswordGenerator.MinimumLength}-{PasswordGenerator.MaximumLength}).",
        DefaultValueFactory = _ => 20
    };

    private static readonly Option<int> Count = new("--count", "-n")
    {
        Description = "How many passwords to generate (1-100).",
        DefaultValueFactory = _ => 1
    };

    private static readonly Option<bool> NoUppercase = new("--no-uppercase") { Description = "Exclude A-Z." };
    private static readonly Option<bool> NoLowercase = new("--no-lowercase") { Description = "Exclude a-z." };
    private static readonly Option<bool> NoNumbers = new("--no-numbers") { Description = "Exclude 0-9." };
    private static readonly Option<bool> NoSpecial = new("--no-special") { Description = "Exclude special characters." };
    private static readonly Option<bool> ExcludeAmbiguous = new("--exclude-ambiguous")
    {
        Description = "Exclude look-alike characters (0 O o 1 l I)."
    };

    public static Command Create()
    {
        var command = new Command("genpass", "Generate strong random passwords.")
        {
            Length, Count, NoUppercase, NoLowercase, NoNumbers, NoSpecial, ExcludeAmbiguous
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(parse, ct, "genpass", ctx =>
        {
            var count = ctx.Parse.GetValue(Count);
            if (count is < 1 or > 100)
                throw new CliException("invalid_argument", "--count must be between 1 and 100.", ExitCodes.InvalidUsage);
            var options = new PasswordGeneratorOptions(
                ctx.Parse.GetValue(Length),
                !ctx.Parse.GetValue(NoUppercase),
                !ctx.Parse.GetValue(NoLowercase),
                !ctx.Parse.GetValue(NoNumbers),
                !ctx.Parse.GetValue(NoSpecial),
                ctx.Parse.GetValue(ExcludeAmbiguous));
            var passwords = Enumerable.Range(0, count).Select(_ => PasswordGenerator.Generate(options)).ToArray();

            ctx.Reporter.Emit("result", Fields.Of(
                ("operation", "genpass"),
                ("status", "succeeded"),
                ("exitCode", ExitCodes.Success),
                ("passwords", passwords)));
            ctx.Reporter.Result(string.Join(Environment.NewLine, passwords));
            return Task.FromResult(ExitCodes.Success);
        }));
        return command;
    }
}
