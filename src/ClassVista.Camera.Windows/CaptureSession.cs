using System.Diagnostics;
using ClassVista.Camera.Abstractions;
using ClassVista.Diagnostics;

namespace ClassVista.Camera.Windows;

public sealed class CaptureSession : IAsyncDisposable
{
    private readonly Func<ICameraSource> sourceFactory;
    private readonly CancellationTokenSource cancellation = new();
    private readonly bool paced;
    private Task? worker;
    private string status = "待命";
    private long sequence;

    public CaptureSession(CameraSettings settings, Func<ICameraSource> sourceFactory, bool paced = false)
    {
        settings.Validate();
        Settings = settings;
        this.sourceFactory = sourceFactory;
        this.paced = paced;
    }

    public CameraSettings Settings { get; }
    public LatestFrameQueue Frames { get; } = new(2);
    public CaptureMetrics Metrics { get; private set; } = new(Stopwatch.GetTimestamp());
    public string Status => Volatile.Read(ref status);
    public bool IsSimulation => paced;

    public void Start()
    {
        if (worker is not null)
            throw new InvalidOperationException("取像工作已啟動。");
        Metrics = new CaptureMetrics(Stopwatch.GetTimestamp());
        worker = Task.Run(RunAsync);
    }

    public void RequestStop() => cancellation.Cancel();

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        if (worker is not null)
            await worker.ConfigureAwait(false);
        Frames.Dispose();
        cancellation.Dispose();
    }

    private async Task RunAsync()
    {
        var attempted = false;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    using var source = sourceFactory();
                    Volatile.Write(ref status, "開啟攝影機中");
                    source.Open(Settings);
                    if (attempted)
                        Metrics.Reconnected();
                    attempted = true;
                    var nextFrameTimestamp = (double)Stopwatch.GetTimestamp();
                    while (!cancellation.IsCancellationRequested)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var frame = source.Read(++sequence);
                        Metrics.Observe(frame.ArrivalTimestamp, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        Frames.Enqueue(frame);
                        Volatile.Write(ref status, source.Status);
                        if (paced)
                        {
                            nextFrameTimestamp += (double)Stopwatch.Frequency / Settings.FramesPerSecond;
                            var now = Stopwatch.GetTimestamp();
                            var remaining = TimeSpan.FromSeconds((nextFrameTimestamp - now) / Stopwatch.Frequency);
                            if (remaining > TimeSpan.Zero)
                                await Task.Delay(remaining, cancellation.Token).ConfigureAwait(false);
                            else
                                nextFrameTimestamp = now;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    attempted = true;
                    Metrics.ReadFailed();
                    Frames.Clear();
                    Volatile.Write(ref status, $"中斷：{exception.Message}（2 秒後重試）");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref status, "已停止");
        }
    }
}