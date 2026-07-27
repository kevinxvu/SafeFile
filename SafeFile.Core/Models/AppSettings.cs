using SafeFile.Core.Crypto;

namespace SafeFile.Core.Models;

public sealed class AppSettings
{
    public int DefaultChunkSizeMb { get; set; } = 1;
    public int MaxThreads { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);
    public string CpuPriority { get; set; } = "Normal";

    public int Argon2MemorySizeKb { get; set; } = 65_536;
    public int Argon2Iterations { get; set; } = 4;
    public int Argon2Parallelism { get; set; } = 2;

    public string DefaultOutputPath { get; set; } = string.Empty;
    public string NamingPolicy { get; set; } = "AskMeWhatToDo";
    public bool SecureDeleteAfterEncrypt { get; set; } = false;
    public bool ConfirmPasswordToggle { get; set; } = true;
    public int MinPasswordLength { get; set; } = 8;

    public Argon2Parameters GetKdfParameters()
    {
        return new Argon2Parameters(Argon2MemorySizeKb, Argon2Iterations, Argon2Parallelism);
    }

    public void SetKdfParameters(Argon2Parameters parameters)
    {
        Argon2MemorySizeKb = parameters.MemorySizeKb;
        Argon2Iterations = parameters.Iterations;
        Argon2Parallelism = parameters.Parallelism;
    }

    public int GetChunkSizeBytes() => DefaultChunkSizeMb * 1_048_576;

    public static AppSettings GetDefaults()
    {
        return new AppSettings
        {
            DefaultChunkSizeMb = 1,
            MaxThreads = Math.Max(1, Environment.ProcessorCount - 1),
            CpuPriority = "Normal",
            Argon2MemorySizeKb = 65_536,
            Argon2Iterations = 4,
            Argon2Parallelism = 2,
            DefaultOutputPath = GetDefaultOutputPath(),
            NamingPolicy = "AskMeWhatToDo",
            SecureDeleteAfterEncrypt = false,
            ConfirmPasswordToggle = true,
            MinPasswordLength = 8
        };
    }

    private static string GetDefaultOutputPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SafeFile");
    }
}
