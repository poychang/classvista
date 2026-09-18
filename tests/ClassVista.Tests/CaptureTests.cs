using System.Buffers;
using System.Diagnostics;
using ClassVista.Camera.Abstractions;
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

    private sealed class TrackingPool : ArrayPool<byte>
    {
        public int Returned { get; private set; }
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false) => Returned++;
    }
}