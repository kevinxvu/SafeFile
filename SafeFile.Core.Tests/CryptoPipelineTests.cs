using SafeFile.Core.Crypto;
using SafeFile.Core.Pipeline;

namespace SafeFile.Core.Tests;

public sealed class CryptoPipelineTests
{
    [Fact]
    public async Task MultipleConsumers_WriteEveryChunkInOrder()
    {
        const int chunkCount = 250;
        var written = new List<long>();
        var key = Enumerable.Repeat((byte)0x42, 32).ToArray();
        var noncePrefix = new byte[] { 1, 2, 3, 4 };
        var pipeline = new CryptoPipeline(consumerCount: 8);

        await pipeline.EncryptAsync(
            async index =>
            {
                if (index > chunkCount)
                    return null;
                await Task.Delay((int)(index % 5));
                return new UnencryptedChunk(index, BitConverter.GetBytes(index), index == chunkCount);
            },
            chunk =>
            {
                written.Add(chunk.Index);
                return Task.CompletedTask;
            },
            key,
            noncePrefix,
            totalChunks: chunkCount,
            startingIndex: 1);

        Assert.Equal(Enumerable.Range(1, chunkCount).Select(i => (long)i), written);
    }

    [Fact]
    public async Task DecryptAsync_RejectsStreamWithoutFinalChunk()
    {
        var key = Enumerable.Repeat((byte)0x24, 32).ToArray();
        var noncePrefix = new byte[] { 4, 3, 2, 1 };
        var encrypted = new AesGcmEngine().EncryptChunk(
            "truncated"u8, key, noncePrefix, chunkIndex: 1, isLastChunk: false);
        var pipeline = new CryptoPipeline(consumerCount: 2);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            pipeline.DecryptAsync(
                index => Task.FromResult(index == 1 ? encrypted : null),
                _ => Task.CompletedTask,
                key,
                startingIndex: 1));
    }

    [Fact]
    public async Task DecryptAsync_RejectsDataAfterFinalChunk()
    {
        var key = Enumerable.Repeat((byte)0x36, 32).ToArray();
        var noncePrefix = new byte[] { 8, 6, 4, 2 };
        var engine = new AesGcmEngine();
        var chunks = new[]
        {
            engine.EncryptChunk("first"u8, key, noncePrefix, 1, isLastChunk: true),
            engine.EncryptChunk("extra"u8, key, noncePrefix, 2, isLastChunk: false)
        };
        var pipeline = new CryptoPipeline(consumerCount: 2);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            pipeline.DecryptAsync(
                index => Task.FromResult(index is >= 1 and <= 2 ? chunks[index - 1] : null),
                _ => Task.CompletedTask,
                key,
                startingIndex: 1));
    }

    [Fact]
    public async Task EncryptAsync_RejectsStreamWithoutFinalChunk()
    {
        var key = Enumerable.Repeat((byte)0x52, 32).ToArray();
        var pipeline = new CryptoPipeline(consumerCount: 2);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            pipeline.EncryptAsync(
                index => Task.FromResult<UnencryptedChunk?>(
                    index == 1 ? new(index, "incomplete"u8.ToArray(), false) : null),
                _ => Task.CompletedTask,
                key,
                new byte[] { 1, 3, 5, 7 },
                startingIndex: 1));
    }

    [Fact]
    public async Task EncryptAsync_RejectsDataAfterFinalChunk()
    {
        var key = Enumerable.Repeat((byte)0x64, 32).ToArray();
        var pipeline = new CryptoPipeline(consumerCount: 2);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            pipeline.EncryptAsync(
                index => Task.FromResult<UnencryptedChunk?>(index switch
                {
                    1 => new(index, "final"u8.ToArray(), true),
                    2 => new(index, "extra"u8.ToArray(), false),
                    _ => null
                }),
                _ => Task.CompletedTask,
                key,
                new byte[] { 2, 4, 6, 8 },
                startingIndex: 1));
    }
}
