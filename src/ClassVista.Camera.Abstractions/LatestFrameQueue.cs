namespace ClassVista.Camera.Abstractions;

public sealed class LatestFrameQueue : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<CameraFrame> frames = new();
    private readonly int capacity;
    private bool disposed;
    private long dropped;

    public LatestFrameQueue(int capacity = 2)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public long Dropped { get { lock (gate) return dropped; } }
    public int Count { get { lock (gate) return frames.Count; } }

    // 呼叫後由佇列接管影格；取出最新影格時再移交給消費者。
    public void Enqueue(CameraFrame frame)
    {
        lock (gate)
        {
            if (disposed)
            {
                frame.Dispose();
                throw new ObjectDisposedException(nameof(LatestFrameQueue));
            }
            if (frames.Count == capacity)
            {
                frames.Dequeue().Dispose();
                dropped++;
            }
            frames.Enqueue(frame);
        }
    }

    public CameraFrame? TakeLatest()
    {
        lock (gate)
        {
            while (frames.Count > 1)
            {
                frames.Dequeue().Dispose();
                dropped++;
            }
            return frames.TryDequeue(out var frame) ? frame : null;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            while (frames.TryDequeue(out var frame))
            {
                frame.Dispose();
                dropped++;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Clear();
        }
    }
}