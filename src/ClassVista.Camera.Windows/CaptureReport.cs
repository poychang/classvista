using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClassVista.Camera.Windows;

public sealed class CaptureReport : IAsyncDisposable
{
    private readonly IReadOnlyList<CaptureSession> sessions;
    private readonly StreamWriter writer;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Process process = Process.GetCurrentProcess();
    private TimeSpan previousCpu;
    private long previousTimestamp = Stopwatch.GetTimestamp();

    public CaptureReport(string path, IReadOnlyList<CaptureSession> sessions)
    {
        this.sessions = sessions;
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            65536, FileOptions.Asynchronous), new UTF8Encoding(false));
        worker = WriteAsync();
    }

    public string? Error { get; private set; }

    private async Task WriteAsync()
    {
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                Type = "metadata", SchemaVersion = 1, StartedUtc = DateTimeOffset.UtcNow,
                Stopwatch.Frequency, OS = Environment.OSVersion.ToString(), Runtime = Environment.Version.ToString(),
                CameraSettings = sessions.Select(session => new { session.Settings, session.IsSimulation }),
                TimestampMeaning = "Host arrival after read; not sensor exposure or end-to-end latency",
                DropMeaning = "Software preview queue discards; sensor/USB dropped frames unknown"
            }));
            previousCpu = process.TotalProcessorTime;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellation.Token))
                    await WriteSampleAsync("sample");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            await WriteSampleAsync("final");
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
    }

    private async Task WriteSampleAsync(string type)
    {
        var now = Stopwatch.GetTimestamp();
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        var elapsed = Stopwatch.GetElapsedTime(previousTimestamp, now).TotalSeconds;
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            Type = type, Utc = DateTimeOffset.UtcNow,
            ElapsedSeconds = Stopwatch.GetElapsedTime(started, now).TotalSeconds,
            CpuPercent = elapsed > 0 ? (cpu - previousCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100 : 0,
            process.WorkingSet64, process.PrivateMemorySize64,
            Cameras = sessions.Select(session => new
            {
                session.Settings.DeviceId, session.Status, session.IsSimulation,
                Metrics = session.Metrics.Snapshot(now), QueueDepth = session.Frames.Count,
                PreviewQueueDiscards = session.Frames.Dropped
            })
        }));
        await writer.FlushAsync();
        previousTimestamp = now;
        previousCpu = cpu;
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        await worker.ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);
        process.Dispose();
        cancellation.Dispose();
    }
}