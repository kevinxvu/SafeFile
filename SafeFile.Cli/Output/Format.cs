using System.Globalization;

namespace SafeFile.Cli.Output;

internal static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : value.ToString("0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    public static double Round(double value, int digits = 2) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
