namespace SafeFile.Core.Pipeline;

public sealed record UnencryptedChunk(long Index, byte[] Data, bool IsLastChunk);
