using System.Collections.Concurrent;
using System.Threading.Channels;
using SafeFile.Core.Crypto;

namespace SafeFile.Core.Pipeline;

public sealed class CryptoPipeline
{
    private readonly AesGcmEngine _aesGcm = new();
    private readonly int _consumerCount;
    private readonly IProgress<double>? _progress;
    private long _totalChunksExpected;

    public CryptoPipeline(int consumerCount = -1, IProgress<double>? progress = null, long totalChunksExpected = 0)
    {
        _consumerCount = consumerCount > 0 ? consumerCount : Math.Max(1, Environment.ProcessorCount - 1);
        _progress = progress;
        _totalChunksExpected = totalChunksExpected > 0 ? totalChunksExpected : 1;
    }

    public async Task EncryptAsync(
        Func<long, Task<UnencryptedChunk?>> sourceReader,
        Func<EncryptedChunk, Task> outputWriter,
        byte[] masterKey,
        byte[] noncePrefix,
        long? totalChunks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceReader);
        ArgumentNullException.ThrowIfNull(outputWriter);
        ArgumentNullException.ThrowIfNull(masterKey);
        ArgumentNullException.ThrowIfNull(noncePrefix);

        if (masterKey.Length != 32)
            throw new ArgumentException("Master key must be 32 bytes.", nameof(masterKey));

        if (noncePrefix.Length != 4)
            throw new ArgumentException("Nonce prefix must be 4 bytes.", nameof(noncePrefix));

        // Update total chunks for progress tracking
        if (totalChunks.HasValue && totalChunks.Value > 0)
        {
            _totalChunksExpected = totalChunks.Value;
        }

        // Use bounded channels to prevent unbounded buffer growth (OOM prevention)
        var unencryptedChannelOptions = new BoundedChannelOptions(capacity: 100)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        var unencryptedChannel = Channel.CreateBounded<UnencryptedChunk>(unencryptedChannelOptions);

        var encryptedChannelOptions = new BoundedChannelOptions(capacity: 100)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        var encryptedChannel = Channel.CreateBounded<EncryptedChunk>(encryptedChannelOptions);

        try
        {
            var producerTask = ProducerAsync(sourceReader, unencryptedChannel, cancellationToken);
            var consumersTask = ConsumersAsync(unencryptedChannel, encryptedChannel, masterKey, noncePrefix, cancellationToken);
            var writerTask = WriterAsync(encryptedChannel, outputWriter, cancellationToken);

            await Task.WhenAll(producerTask, consumersTask, writerTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            unencryptedChannel.Writer.TryComplete();
            encryptedChannel.Writer.TryComplete();
        }
    }

    public async Task DecryptAsync(
        Func<long, Task<EncryptedChunk?>> sourceReader,
        Func<byte[], Task> outputWriter,
        byte[] masterKey,
        long? totalChunks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceReader);
        ArgumentNullException.ThrowIfNull(outputWriter);
        ArgumentNullException.ThrowIfNull(masterKey);

        if (masterKey.Length != 32)
            throw new ArgumentException("Master key must be 32 bytes.", nameof(masterKey));

        // Update total chunks for progress tracking
        if (totalChunks.HasValue && totalChunks.Value > 0)
        {
            _totalChunksExpected = totalChunks.Value;
        }

        // Use bounded channel to prevent unbounded buffer growth (OOM prevention)
        var decryptedChannelOptions = new BoundedChannelOptions(capacity: 100)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        var decryptedChannel = Channel.CreateBounded<DecryptedChunk>(decryptedChannelOptions);

        try
        {
            var producerTask = DecryptProducerAsync(sourceReader, decryptedChannel, cancellationToken);
            var consumersTask = DecryptConsumersAsync(decryptedChannel, masterKey, outputWriter, cancellationToken);

            await Task.WhenAll(producerTask, consumersTask).ConfigureAwait(false);
        }
        finally
        {
            decryptedChannel.Writer.TryComplete();
        }
    }

    private async Task ProducerAsync(
        Func<long, Task<UnencryptedChunk?>> reader,
        Channel<UnencryptedChunk> channel,
        CancellationToken cancellationToken)
    {
        try
        {
            long index = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var chunk = await reader(index).ConfigureAwait(false);
                if (chunk is null)
                    break;

                await channel.Writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                index++;
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private async Task ConsumersAsync(
        Channel<UnencryptedChunk> inputChannel,
        Channel<EncryptedChunk> outputChannel,
        byte[] masterKey,
        byte[] noncePrefix,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        for (int i = 0; i < _consumerCount; i++)
        {
            tasks.Add(ConsumerWorkerAsync(inputChannel, outputChannel, masterKey, noncePrefix, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        outputChannel.Writer.TryComplete();
    }

    private async Task ConsumerWorkerAsync(
        Channel<UnencryptedChunk> inputChannel,
        Channel<EncryptedChunk> outputChannel,
        byte[] masterKey,
        byte[] noncePrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var plainChunk in inputChannel.Reader.ReadAllAsync(cancellationToken))
            {
                var encryptedChunk = _aesGcm.EncryptChunk(plainChunk.Data, masterKey, noncePrefix, plainChunk.Index, plainChunk.IsLastChunk);
                await outputChannel.Writer.WriteAsync(encryptedChunk, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            outputChannel.Writer.TryComplete();
        }
    }

    private async Task WriterAsync(
        Channel<EncryptedChunk> encryptedChannel,
        Func<EncryptedChunk, Task> outputWriter,
        CancellationToken cancellationToken)
    {
        var buffer = new Dictionary<long, EncryptedChunk>();
        long nextIndexToWrite = 0;

        try
        {
            await foreach (var chunk in encryptedChannel.Reader.ReadAllAsync(cancellationToken))
            {
                if (chunk.Index == nextIndexToWrite)
                {
                    await outputWriter(chunk).ConfigureAwait(false);
                    ReportProgress(nextIndexToWrite + 1);
                    nextIndexToWrite++;

                    while (buffer.Remove(nextIndexToWrite, out var bufferedChunk))
                    {
                        await outputWriter(bufferedChunk).ConfigureAwait(false);
                        ReportProgress(nextIndexToWrite + 1);
                        nextIndexToWrite++;
                    }
                }
                else if (chunk.Index > nextIndexToWrite)
                {
                    buffer[chunk.Index] = chunk;
                }
                else if (chunk.Index < nextIndexToWrite)
                {
                    // Duplicate or out-of-order chunk that arrived after it should have been written
                    throw new InvalidOperationException(
                        $"Out-of-order chunk received: chunk index {chunk.Index} is less than next expected index {nextIndexToWrite}. " +
                        $"This indicates data corruption or a timing error in the encryption pipeline.");
                }
            }

            if (buffer.Count > 0)
            {
                throw new InvalidOperationException($"Incomplete chunk stream: missing chunks before index {nextIndexToWrite}.");
            }
        }
        finally
        {
            encryptedChannel.Reader.Completion.Wait(TimeSpan.FromSeconds(5));
        }
    }

    private async Task DecryptProducerAsync(
        Func<long, Task<EncryptedChunk?>> reader,
        Channel<DecryptedChunk> channel,
        CancellationToken cancellationToken)
    {
        try
        {
            long index = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var chunk = await reader(index).ConfigureAwait(false);
                if (chunk is null)
                    break;

                await channel.Writer.WriteAsync(
                    new DecryptedChunk(chunk.Index, chunk, chunk.IsLastChunk),
                    cancellationToken).ConfigureAwait(false);
                index++;
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private async Task DecryptConsumersAsync(
        Channel<DecryptedChunk> channel,
        byte[] masterKey,
        Func<byte[], Task> outputWriter,
        CancellationToken cancellationToken)
    {
        var buffer = new Dictionary<long, DecryptedChunk>();
        long nextIndexToWrite = 0;

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (item.Index == nextIndexToWrite)
                {
                    var plaintext = _aesGcm.DecryptChunk(item.EncryptedChunk, masterKey);
                    await outputWriter(plaintext).ConfigureAwait(false);
                    ReportProgress(nextIndexToWrite + 1);
                    nextIndexToWrite++;

                    while (buffer.Remove(nextIndexToWrite, out var bufferedItem))
                    {
                        plaintext = _aesGcm.DecryptChunk(bufferedItem.EncryptedChunk, masterKey);
                        await outputWriter(plaintext).ConfigureAwait(false);
                        ReportProgress(nextIndexToWrite + 1);
                        nextIndexToWrite++;
                    }
                }
                else if (item.Index > nextIndexToWrite)
                {
                    buffer[item.Index] = item;
                }
                else if (item.Index < nextIndexToWrite)
                {
                    // Duplicate or out-of-order chunk that arrived after it should have been written
                    throw new InvalidOperationException(
                        $"Out-of-order chunk received: chunk index {item.Index} is less than next expected index {nextIndexToWrite}. " +
                        $"This indicates data corruption or a timing error in the decryption pipeline.");
                }
            }
        }
        finally
        {
            channel.Reader.Completion.Wait(TimeSpan.FromSeconds(5));
        }
    }

    private void ReportProgress(long processedChunks)
    {
        if (_progress is not null && _totalChunksExpected > 0)
        {
            var progress = (double)processedChunks / _totalChunksExpected;
            _progress.Report(Math.Min(progress, 1.0));
        }
    }
}

internal sealed record DecryptedChunk(long Index, EncryptedChunk EncryptedChunk, bool IsLastChunk);
