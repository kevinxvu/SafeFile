using System;
using System.Diagnostics;

namespace SafeFile.Services;

/// <summary>
/// Estimates speed from bytes actually transferred and ETA from workload that
/// has been resolved. Skipped work therefore cannot create artificial speed.
/// </summary>
public sealed class TransferMetricsEstimator
{
    private static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WarmupDuration = TimeSpan.FromSeconds(1.5);
    private const double SmoothingFactor = 0.2;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private TimeSpan _lastSampleTime;
    private long _lastTransferredBytes;
    private double? _smoothedBytesPerSecond;
    private int _sampleCount;

    public TransferMetricsEstimate Update(
        long transferredBytes,
        long totalWorkloadBytes,
        long resolvedWorkloadBytes,
        bool includeSample = true)
    {
        totalWorkloadBytes = Math.Max(0, totalWorkloadBytes);
        transferredBytes = Math.Max(_lastTransferredBytes, transferredBytes);
        resolvedWorkloadBytes = Math.Clamp(resolvedWorkloadBytes, 0, totalWorkloadBytes);

        var now = _stopwatch.Elapsed;
        var elapsed = now - _lastSampleTime;
        var deltaBytes = transferredBytes - _lastTransferredBytes;

        if (!includeSample && deltaBytes > 0)
        {
            _lastSampleTime = now;
            _lastTransferredBytes = transferredBytes;
        }
        else if (elapsed >= MinimumSampleInterval && deltaBytes > 0)
        {
            var instantaneousSpeed = deltaBytes / elapsed.TotalSeconds;
            _smoothedBytesPerSecond = _smoothedBytesPerSecond is { } previous
                ? SmoothingFactor * instantaneousSpeed + (1 - SmoothingFactor) * previous
                : instantaneousSpeed;
            _sampleCount++;
            _lastSampleTime = now;
            _lastTransferredBytes = transferredBytes;
        }

        var isReady = _stopwatch.Elapsed >= WarmupDuration &&
                      _sampleCount >= 2 &&
                      _smoothedBytesPerSecond is > 0;
        var remainingSeconds = isReady
            ? Math.Max(0, (totalWorkloadBytes - resolvedWorkloadBytes) /
                _smoothedBytesPerSecond!.Value)
            : (double?)null;

        return new TransferMetricsEstimate(_smoothedBytesPerSecond, remainingSeconds);
    }
}

public readonly record struct TransferMetricsEstimate(
    double? BytesPerSecond,
    double? RemainingSeconds);
