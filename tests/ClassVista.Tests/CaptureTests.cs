using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ClassVista.Camera.Abstractions;
using ClassVista.Camera.Windows;
using ClassVista.Diagnostics;
using Xunit;

namespace ClassVista.Tests;

public sealed class CaptureTests
{
    [Fact]
    public void FullQueueDropsOldestAndReturnsEveryBufferExactlyOnce()
    {
        var pool = new TrackingPool();
        using var queue = new LatestFrameQueue(2);
        queue.Enqueue(new CameraFrame("left", 1, 1, 2, 2, pool));
        queue.Enqueue(new CameraFrame("left", 2, 2, 2, 2, pool));
        queue.Enqueue(new CameraFrame("left", 3, 3, 2, 2, pool));
        Assert.Equal(1, pool.Returned);
        Assert.Equal(2, queue.Count);
        var latest = queue.TakeLatest();
        Assert.NotNull(latest);
        Assert.Equal(3, latest.Sequence);
        Assert.Equal(2, queue.Dropped);
        latest.Dispose();
        latest.Dispose();
        Assert.Equal(3, pool.Returned);
        Assert.Throws<ObjectDisposedException>(() => latest.Pixels);
    }

    [Fact]
    public void DisposedQueueReleasesQueuedAndRejectedFrames()
    {
        var pool = new TrackingPool();
        var queue = new LatestFrameQueue();
        queue.Enqueue(new CameraFrame("camera", 1, 0, 2, 2, pool));
        queue.Dispose();
        Assert.Throws<ObjectDisposedException>(() => queue.Enqueue(new CameraFrame("camera", 2, 0, 2, 2, pool)));
        Assert.Equal(2, pool.Returned);
        Assert.Null(queue.TakeLatest());
    }

    [Fact]
    public void InvalidSettingsAreRejectedBeforeOpeningHardware()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraSettings("camera", Width: 0).Validate());
        Assert.Throws<ArgumentException>(() => new CameraSettings("camera", PixelFormat: "H264").Validate());
        Assert.Throws<ArgumentException>(() => new CameraSettings("camera", Exposure: double.NaN).Validate());
        new CameraSettings("camera").Validate();
    }

    [Fact]
    public void MetricsIncludeStallsAndBoundTheRecentSampleWindow()
    {
        var metrics = new CaptureMetrics(0);
        for (var index = 1; index <= 150; index++)
            metrics.Observe(index * Stopwatch.Frequency, index);
        metrics.ReadFailed();
        metrics.Reconnected();
        var result = metrics.Snapshot(160 * Stopwatch.Frequency);
        Assert.Equal(150, result.Frames);
        Assert.Equal(0.9375, result.AverageFps);
        Assert.Equal(10000, result.FrameAgeMs);
        Assert.Equal(144, result.ReadP95Ms);
        Assert.Equal(1000, result.MaximumGapMs);
        Assert.Equal(1, result.ReadFailures);
        Assert.Equal(1, result.Reconnects);
        Assert.True(result.RecentFps < 1);
    }

    [Fact]
    public void ConcurrentArrivalCannotProduceNegativeFrameAge()
    {
        var metrics = new CaptureMetrics(0);
        metrics.Observe(2 * Stopwatch.Frequency, 1);
        var snapshot = metrics.Snapshot(Stopwatch.Frequency);
        Assert.Equal(0, snapshot.FrameAgeMs);
        Assert.Equal(0.5, snapshot.AverageFps);
    }

    [Fact]
    public async Task ThreeSyntheticCamerasProduceFramesAndStopWithoutHardware()
    {
        var sessions = Enumerable.Range(0, 3).Select(index => new CaptureSession(
            new CameraSettings($"simulation:{index}", 320, 180), () => new SyntheticCameraSource(), true)).ToArray();
        try
        {
            foreach (var session in sessions)
                session.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (sessions.Any(session => session.Metrics.Snapshot(Stopwatch.GetTimestamp()).Frames < 5))
                await Task.Delay(20, timeout.Token);
            foreach (var session in sessions)
            {
                using var frame = session.Frames.TakeLatest();
                Assert.NotNull(frame);
                Assert.Equal(320 * 180 * 3, frame.Length);
                Assert.Contains(frame.Pixels.Take(frame.Length), pixel => pixel > 0);
                Assert.Equal(0, session.Metrics.Snapshot(Stopwatch.GetTimestamp()).ReadFailures);
            }
        }
        finally
        {
            foreach (var session in sessions)
                session.RequestStop();
            foreach (var session in sessions)
                await session.DisposeAsync();
        }
        Assert.All(sessions, session => Assert.Equal("已停止", session.Status));
    }

    [Fact]
    public async Task FailedOpenReconnectsAndWritesFinalReport()
    {
        var attempts = 0;
        var path = Path.Combine(Path.GetTempPath(), $"classvista-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var session = new CaptureSession(new CameraSettings("simulation:test", 320, 180),
                () => Interlocked.Increment(ref attempts) == 1 ? new UnavailableSource() : new SyntheticCameraSource(), true))
            {
                session.Start();
                await using (var report = new CaptureReport(path, new[] { session }))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while (session.Metrics.Snapshot(Stopwatch.GetTimestamp()).Frames < 3)
                        await Task.Delay(20, timeout.Token);
                    Assert.Null(report.Error);
                }
                var metrics = session.Metrics.Snapshot(Stopwatch.GetTimestamp());
                Assert.Equal(1, metrics.ReadFailures);
                Assert.Equal(1, metrics.Reconnects);
            }
            var lines = await File.ReadAllLinesAsync(path);
            using var metadata = JsonDocument.Parse(lines[0]);
            using var final = JsonDocument.Parse(lines[^1]);
            Assert.Equal("metadata", metadata.RootElement.GetProperty("Type").GetString());
            Assert.Equal("final", final.RootElement.GetProperty("Type").GetString());
            Assert.True(final.RootElement.GetProperty("Cameras")[0].GetProperty("IsSimulation").GetBoolean());
            Assert.True(final.RootElement.GetProperty("Cameras")[0].GetProperty("Metrics").GetProperty("Frames").GetInt64() >= 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StopCancelsReconnectDelayPromptly()
    {
        var session = new CaptureSession(new CameraSettings("missing"), () => new UnavailableSource());
        session.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (session.Metrics.Snapshot(Stopwatch.GetTimestamp()).ReadFailures == 0)
            await Task.Delay(10, timeout.Token);
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("已停止", session.Status);
    }

    private sealed class UnavailableSource : ICameraSource
    {
        public string Status => "離線";
        public void Open(CameraSettings settings) => throw new IOException("測試用中斷");
        public CameraFrame Read(long sequence) => throw new IOException("測試用中斷");
        public void Dispose() { }
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        public int Returned { get; private set; }
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false) => Returned++;
    }
}