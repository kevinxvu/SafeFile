using System.Security.Cryptography;

namespace SafeFile.Core.Services;

public sealed record PasswordGeneratorOptions(
    int Length,
    bool IncludeUppercase,
    bool IncludeLowercase,
    bool IncludeNumbers,
    bool IncludeSpecialCharacters,
    bool ExcludeAmbiguousCharacters);

public static class PasswordGenerator
{
    public const int MinimumLength = 4;
    public const int MaximumLength = 64;

    private const string Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";
    private const string Numbers = "0123456789";
    private const string Special = "!@#$%^&*()-_=+[]{};:,.?/";
    private const string Ambiguous = "0Oo1lI";

    public static string Generate(PasswordGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Length is < MinimumLength or > MaximumLength)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"Password length must be between {MinimumLength} and {MaximumLength}.");

        var groups = new List<string>(4);
        AddGroup(groups, Uppercase, options.IncludeUppercase, options.ExcludeAmbiguousCharacters);
        AddGroup(groups, Lowercase, options.IncludeLowercase, options.ExcludeAmbiguousCharacters);
        AddGroup(groups, Numbers, options.IncludeNumbers, options.ExcludeAmbiguousCharacters);
        AddGroup(groups, Special, options.IncludeSpecialCharacters, false);
        if (groups.Count == 0)
            throw new ArgumentException("Select at least one character group.", nameof(options));
        if (options.Length < groups.Count)
            throw new ArgumentException("Password length is shorter than the selected character group count.", nameof(options));

        var allCharacters = string.Concat(groups);
        var result = new char[options.Length];
        var position = 0;
        foreach (var group in groups)
            result[position++] = group[RandomNumberGenerator.GetInt32(group.Length)];
        while (position < result.Length)
            result[position++] = allCharacters[RandomNumberGenerator.GetInt32(allCharacters.Length)];

        for (var i = result.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }
        return new string(result);
    }

    private static void AddGroup(List<string> groups, string characters, bool enabled, bool excludeAmbiguous)
    {
        if (!enabled)
            return;
        groups.Add(excludeAmbiguous
            ? new string(characters.Where(c => !Ambiguous.Contains(c)).ToArray())
            : characters);
    }
}
