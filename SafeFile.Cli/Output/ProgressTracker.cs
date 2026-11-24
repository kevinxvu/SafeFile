using System.Diagnostics;
using SafeFile.Core.Services;

namespace SafeFile.Cli.Output;

/// <summary>
/// Aggregates progress for one CLI operation and reports it through the
/// <see cref="Reporter"/>, throttled to the configured interval. Speed and ETA
/// use the same <see cref="TransferMetricsEstimator"/> as the desktop UI.
/// All members are thread-safe; Core may report from worker threads.
/// </summary>
internal sealed class ProgressTracker
{
    private const int BarWidth = 24;

    private readonly Reporter _reporter;
    private readonly string _operation;
    private readonly string _unit;
    private readonly TransferMetricsEstimator _estimator = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    // Far enough in the past that the first update is always emitted (MinValue would overflow on subtraction).
    private TimeSpan _lastJsonEmit = TimeSpan.FromDays(-1);
    private TimeSpan _lastTextEmit = TimeSpan.FromDays(-1);
    private long _processed;
    private TransferMetricsEstimate _estimate;
    private string? _currentPath;
    private string _phase;
    private bool _dirty = true;

    public ProgressTracker(Reporter reporter, string operation, string unit, string phase)
    {
        _reporter = reporter;
        _operation = operation;
        _unit = unit;
        _phase = phase;
    }

    public object Gate { get; } = new();
    public long Total { get; set; }
    public int TotalItems { get; set; }
    public int Succeeded { get; private set; }
    public int Failed { get; private set; }
    public double ElapsedSeconds => _stopwatch.Elapsed.TotalSeconds;

    public void SetCurrent(string? path, string? phase = null)
    {
        lock (Gate)
        {
            _currentPath = path;
            if (phase is not null)
                _phase = phase;
            _dirty = true;
        }
    }

    public void SetPhase(string phase)
    {
        lock (Gate)
        {
            _dirty |= _phase != phase;
            _phase = phase;
        }
    }

    public void ItemSucceeded()
    {
        lock (Gate)
        {
            Succeeded++;
            _dirty = true;
        }
    }

    public void ItemFailed()
    {
        lock (Gate)
        {
            Failed++;
            _dirty = true;
        }
    }

    /// <param name="processed">Units actually transferred (drives speed).</param>
    /// <param name="resolved">Units finished, transferred or skipped (drives ETA).</param>
    public void Update(long processed, long resolved, bool includeSample = true, bool force = false)
    {
        lock (Gate)
        {
            _processed = Math.Max(_processed, processed);
            if (Total > 0)
                _processed = Math.Min(_processed, Total);
            _estimate = _estimator.Update(_processed, Total, resolved, includeSample);
            _dirty = true;
            EmitIfDue(force);
        }
    }

    /// <summary>Emits the latest state unless it was already reported.</summary>
    public void Flush()
    {
        lock (Gate)
        {
            if (_dirty)
                EmitIfDue(force: true);
        }
    }

    private double Fraction
    {
        get
        {
            if (Total > 0)
                return Math.Clamp(_processed / (double)Total, 0, 1);
            return TotalItems > 0
                ? Math.Clamp((Succeeded + Failed) / (double)TotalItems, 0, 1)
                : 0;
        }
    }

    private void EmitIfDue(bool force)
    {
        if (!_reporter.ShowProgress)
            return;
        var now = _stopwatch.Elapsed;
        if (_reporter.IsJson)
        {
            if (!force && now - _lastJsonEmit < _reporter.ProgressInterval)
                return;
            _lastJsonEmit = now;
            _dirty = false;
            EmitJson();
        }
        else
        {
            if (!force && now - _lastTextEmit < _reporter.TextProgressInterval)
                return;
            _lastTextEmit = now;
            _dirty = false;
            _reporter.ProgressLine(RenderText());
        }
    }

    private void EmitJson()
    {
        var fraction = Fraction;
        _reporter.Emit("progress", Fields.Of(
            ("operation", _operation),
            ("phase", _phase),
            ("percent", Format.Round(fraction * 100)),
            ("fraction", Format.Round(fraction, 4)),
            ("unit", _unit),
            ("processed", _processed),
            ("total", Total),
            ("rate", _estimate.BytesPerSecond is { } rate ? Format.Round(rate) : null),
            ("etaSeconds", _estimate.RemainingSeconds is { } eta ? Format.Round(eta, 1) : null),
            ("elapsedSeconds", Format.Round(ElapsedSeconds, 1)),
            ("currentPath", _currentPath),
            ("succeeded", Succeeded),
            ("failed", Failed),
            ("remaining", Math.Max(0, TotalItems - Succeeded - Failed)),
            ("totalItems", TotalItems)));
    }

    private string RenderText()
    {
        var fraction = Fraction;
        var filled = (int)Math.Round(fraction * BarWidth);
        var bar = new string('#', filled) + new string('-', BarWidth - filled);
        var amount = _unit == "bytes"
            ? $"{Format.Bytes(_processed)} / {Format.Bytes(Total)}"
            : $"{_processed:N0} / {Total:N0} {_unit}";
        var speed = _estimate.BytesPerSecond is { } rate
            ? _unit == "bytes" ? $"{Format.Bytes((long)rate)}/s" : $"{rate:0.0} {_unit}/s"
            : "";
        var eta = _phase == "finalizing"
            ? "finalizing"
            : _estimate.RemainingSeconds is { } remaining
                ? $"ETA {Format.Duration(remaining)}"
                : "ETA calculating";
        var counts = TotalItems > 1
            ? $"{Succeeded + Failed}/{TotalItems}" + (Failed > 0 ? $" ({Failed} failed)" : "")
            : "";
        var name = _currentPath is null ? "" : Path.GetFileName(_currentPath.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Join("  ", new[]
        {
            $"[{bar}] {fraction * 100,5:0.0}%", amount, speed, eta, counts, name
        }.Where(part => part.Length > 0));
    }
}
