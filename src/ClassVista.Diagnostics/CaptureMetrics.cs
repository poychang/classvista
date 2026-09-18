using System.Diagnostics;

namespace ClassVista.Diagnostics;

public sealed record CaptureSnapshot(long Frames, long ReadFailures, long Reconnects,
    double AverageFps, double RecentFps, double ReadP95Ms, double? FrameAgeMs, double MaximumGapMs);

public sealed class CaptureMetrics(long startedTimestamp)
{
    private readonly object gate = new();
    private readonly Queue<(long Timestamp, double ReadMs)> samples = new();
    private long frames;
    private long failures;
    private long reconnects;
    private long? lastTimestamp;
    private double maximumGapMs;

    public void Observe(long timestamp, double readMilliseconds)
    {
        lock (gate)
        {
            frames++;
            if (lastTimestamp is { } previous)
                maximumGapMs = Math.Max(maximumGapMs, Stopwatch.GetElapsedTime(previous, timestamp).TotalMilliseconds);
            lastTimestamp = timestamp;
            samples.Enqueue((timestamp, readMilliseconds));
            if (samples.Count > 120)
                samples.Dequeue();
        }
    }

    public void ReadFailed() { lock (gate) failures++; }
    public void Reconnected() { lock (gate) reconnects++; }

    public CaptureSnapshot Snapshot(long now)
    {
        lock (gate)
        {
            var elapsed = Stopwatch.GetElapsedTime(startedTimestamp, now).TotalSeconds;
            var durations = samples.Select(sample => sample.ReadMs).Order().ToArray();
            var recentSeconds = samples.Count > 1
                ? Stopwatch.GetElapsedTime(samples.Peek().Timestamp, now).TotalSeconds : 0;
            var age = lastTimestamp is { } last ? Stopwatch.GetElapsedTime(last, now).TotalMilliseconds : (double?)null;
            return new CaptureSnapshot(frames, failures, reconnects,
                elapsed > 0 ? frames / elapsed : 0,
                recentSeconds > 0 ? (samples.Count - 1) / recentSeconds : 0,
                durations.Length > 0 ? durations[(int)Math.Ceiling(durations.Length * 0.95) - 1] : 0,
                age, maximumGapMs);
        }
    }
}