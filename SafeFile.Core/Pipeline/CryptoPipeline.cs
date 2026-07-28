using System.Threading.Channels;
using SafeFile.Core.Crypto;

namespace SafeFile.Core.Pipeline;

internal sealed class CryptoPipeline
{
    private const int ChannelCapacity = 100;
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
        long startingIndex = 0,
        CancellationToken cancellationToken = default,
        bool reportProgress = true,
        Action<double>? progressCallback = null)
    {
        ArgumentNullException.ThrowIfNull(sourceReader);
        ArgumentNullException.ThrowIfNull(outputWriter);
        ValidateKey(masterKey);
        ArgumentNullException.ThrowIfNull(noncePrefix);

        if (noncePrefix.Length != AesGcmEngine.NoncePrefixSize)
            throw new ArgumentException("Nonce prefix must be 4 bytes.", nameof(noncePrefix));
        if (startingIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(startingIndex));

        SetTotalChunks(totalChunks);

        var input = CreateChannel<UnencryptedChunk>();
        var output = CreateChannel<EncryptedChunk>();
        using var inFlight = new SemaphoreSlim(ChannelCapacity, ChannelCapacity);
        using var failureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = failureCts.Token;

        var producer = RunStageAsync(
            () => ProduceAsync(sourceReader, input.Writer, startingIndex, inFlight, token),
            input.Writer,
            failureCts);
        var consumers = RunConsumersAsync(input.Reader, output.Writer, masterKey, noncePrefix, token, failureCts);
        var writer = RunStageAsync<EncryptedChunk>(
            () => WriteOrderedAsync(
                output.Reader,
                outputWriter,
                startingIndex,
                reportProgress,
                progressCallback,
                inFlight,
                token),
            writerToComplete: null,
            failureCts);

        await AwaitPipelineAsync([producer, consumers, writer], cancellationToken).ConfigureAwait(false);
    }

    public async Task DecryptAsync(
        Func<long, Task<EncryptedChunk?>> sourceReader,
        Func<byte[], Task> outputWriter,
        byte[] masterKey,
        long? totalChunks = null,
        long startingIndex = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceReader);
        ArgumentNullException.ThrowIfNull(outputWriter);
        ValidateKey(masterKey);
        if (startingIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(startingIndex));

        SetTotalChunks(totalChunks);

        var input = CreateChannel<EncryptedChunk>();
        using var failureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = failureCts.Token;

        var producer = RunStageAsync(
            () => ProduceEncryptedAsync(sourceReader, input.Writer, startingIndex, token),
            input.Writer,
            failureCts);
        var consumer = RunStageAsync<EncryptedChunk>(
            () => DecryptOrderedAsync(input.Reader, outputWriter, masterKey, startingIndex, token),
            writerToComplete: null,
            failureCts);

        await AwaitPipelineAsync([producer, consumer], cancellationToken).ConfigureAwait(false);
    }

    private static Channel<T> CreateChannel<T>() =>
        Channel.CreateBounded<T>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        });

    private async Task RunConsumersAsync(
        ChannelReader<UnencryptedChunk> reader,
        ChannelWriter<EncryptedChunk> writer,
        byte[] masterKey,
        byte[] noncePrefix,
        CancellationToken cancellationToken,
        CancellationTokenSource failureCts)
    {
        try
        {
            var workers = Enumerable.Range(0, _consumerCount)
                .Select(_ => EncryptWorkerWithCancellationAsync(
                    reader,
                    writer,
                    masterKey,
                    noncePrefix,
                    cancellationToken,
                    failureCts))
                .ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            failureCts.Cancel();
            throw;
        }
    }

    private static async Task RunStageAsync<T>(
        Func<Task> stage,
        ChannelWriter<T>? writerToComplete,
        CancellationTokenSource failureCts)
    {
        try
        {
            await stage().ConfigureAwait(false);
            writerToComplete?.TryComplete();
        }
        catch (Exception ex)
        {
            writerToComplete?.TryComplete(ex);
            failureCts.Cancel();
            throw;
        }
    }

    private static async Task AwaitPipelineAsync(Task[] tasks, CancellationToken callerToken)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch when (!callerToken.IsCancellationRequested)
        {
            var failure = tasks.Select(t => t.Exception?.Flatten().InnerExceptions.FirstOrDefault())
                .FirstOrDefault(ex => ex is not null and not OperationCanceledException);
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private static async Task ProduceAsync(
        Func<long, Task<UnencryptedChunk?>> reader,
        ChannelWriter<UnencryptedChunk> writer,
        long startingIndex,
        SemaphoreSlim inFlight,
        CancellationToken cancellationToken)
    {
        for (var index = startingIndex; ; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
            var handedOff = false;
            try
            {
                var chunk = await reader(index).ConfigureAwait(false);
                if (chunk is null)
                    return;
                if (chunk.Index != index)
                    throw new InvalidDataException($"Source returned chunk {chunk.Index}; expected {index}.");
                await writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                handedOff = true;
            }
            finally
            {
                if (!handedOff)
                    inFlight.Release();
            }
        }
    }

    private async Task EncryptWorkerAsync(
        ChannelReader<UnencryptedChunk> reader,
        ChannelWriter<EncryptedChunk> writer,
        byte[] masterKey,
        byte[] noncePrefix,
        CancellationToken cancellationToken)
    {
        await foreach (var chunk in reader.ReadAllAsync(cancellationToken))
        {
            var encrypted = _aesGcm.EncryptChunk(
                chunk.Data, masterKey, noncePrefix, chunk.Index, chunk.IsLastChunk);
            await writer.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EncryptWorkerWithCancellationAsync(
        ChannelReader<UnencryptedChunk> reader,
        ChannelWriter<EncryptedChunk> writer,
        byte[] masterKey,
        byte[] noncePrefix,
        CancellationToken cancellationToken,
        CancellationTokenSource failureCts)
    {
        try
        {
            await EncryptWorkerAsync(
                reader, writer, masterKey, noncePrefix, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            failureCts.Cancel();
            throw;
        }
    }

    private async Task WriteOrderedAsync(
        ChannelReader<EncryptedChunk> reader,
        Func<EncryptedChunk, Task> outputWriter,
        long startingIndex,
        bool reportProgress,
        Action<double>? progressCallback,
        SemaphoreSlim inFlight,
        CancellationToken cancellationToken)
    {
        var buffer = new Dictionary<long, EncryptedChunk>();
        var nextIndex = startingIndex;
        var sawLastChunk = false;

        await foreach (var chunk in reader.ReadAllAsync(cancellationToken))
        {
            if (chunk.Index < nextIndex || !buffer.TryAdd(chunk.Index, chunk))
                throw new InvalidDataException($"Duplicate or stale chunk index {chunk.Index}.");

            while (buffer.Remove(nextIndex, out var ready))
            {
                if (sawLastChunk)
                    throw new InvalidDataException("Unencrypted stream contains data after the final chunk.");
                await outputWriter(ready).ConfigureAwait(false);
                sawLastChunk = ready.IsLastChunk;
                inFlight.Release();
                if (reportProgress)
                    ReportProgress(nextIndex - startingIndex + 1, progressCallback);
                nextIndex++;
            }
        }

        if (buffer.Count != 0)
            throw new InvalidDataException($"Incomplete chunk stream before index {nextIndex}.");
        if (!sawLastChunk)
            throw new InvalidDataException("Unencrypted stream is truncated or has no final chunk.");
    }

    private static async Task ProduceEncryptedAsync(
        Func<long, Task<EncryptedChunk?>> reader,
        ChannelWriter<EncryptedChunk> writer,
        long startingIndex,
        CancellationToken cancellationToken)
    {
        for (var index = startingIndex; ; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = await reader(index).ConfigureAwait(false);
            if (chunk is null)
                return;
            await writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DecryptOrderedAsync(
        ChannelReader<EncryptedChunk> reader,
        Func<byte[], Task> outputWriter,
        byte[] masterKey,
        long startingIndex,
        CancellationToken cancellationToken)
    {
        var expectedIndex = startingIndex;
        var sawLastChunk = false;
        await foreach (var chunk in reader.ReadAllAsync(cancellationToken))
        {
            if (sawLastChunk)
                throw new InvalidDataException("Encrypted stream contains data after the final chunk.");
            if (chunk.Index != expectedIndex)
                throw new InvalidDataException($"Chunk index mismatch: expected {expectedIndex}, got {chunk.Index}.");

            var plaintext = _aesGcm.DecryptChunk(chunk, masterKey);
            await outputWriter(plaintext).ConfigureAwait(false);
            sawLastChunk = chunk.IsLastChunk;
            ReportProgress(expectedIndex - startingIndex + 1);
            expectedIndex++;
        }

        if (!sawLastChunk)
            throw new InvalidDataException("Encrypted stream is truncated or has no final chunk.");
    }

    private static void ValidateKey(byte[] masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        if (masterKey.Length != Argon2Kdf.KeySize)
            throw new ArgumentException("Master key must be 32 bytes.", nameof(masterKey));
    }

    private void SetTotalChunks(long? totalChunks)
    {
        if (totalChunks is > 0)
            _totalChunksExpected = totalChunks.Value;
    }

    private void ReportProgress(long processedChunks)
        => ReportProgress(processedChunks, progressCallback: null);

    private void ReportProgress(long processedChunks, Action<double>? progressCallback)
    {
        if (_totalChunksExpected <= 0)
            return;

        var value = Math.Min((double)processedChunks / _totalChunksExpected, 1.0);
        _progress?.Report(value);
        progressCallback?.Invoke(value);
    }

    internal void ValidateMemoryBudget(int chunkSizeBytes)
    {
        var availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (availableMemoryBytes <= 0)
            throw new InvalidOperationException("Unable to determine the available memory budget.");

        // At most ChannelCapacity chunks can be anywhere in the channels/reorder
        // buffer. Each worker can temporarily hold plaintext and ciphertext; the
        // folder ZIP pipe can retain up to four additional chunk-sized buffers.
        var maximumResidentChunks = checked(ChannelCapacity + (2L * _consumerCount) + 4);
        var requiredBytes = checked((long)chunkSizeBytes * maximumResidentChunks);
        var maximumAllowedBytes = availableMemoryBytes / 2;
        if (requiredBytes > maximumAllowedBytes)
        {
            throw new InvalidOperationException(
                $"Chunk size {chunkSizeBytes:N0} bytes with up to {maximumResidentChunks} resident buffers " +
                $"requires {requiredBytes:N0} bytes, exceeding half of available memory " +
                $"({maximumAllowedBytes:N0} bytes).");
        }
    }
}
