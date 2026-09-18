using System.Buffers;

namespace ClassVista.Camera.Abstractions;

public sealed record CameraDevice(string Id, string Name, int Index);

public sealed record CameraSettings(
    string DeviceId,
    int Width = 1920,
    int Height = 1080,
    int FramesPerSecond = 30,
    string PixelFormat = "MJPG",
    double? Exposure = null,
    double? WhiteBalance = null,
    double? Focus = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DeviceId);
        if (Width <= 0 || Height <= 0 || Width > 7680 || Height > 4320)
            throw new ArgumentOutOfRangeException(nameof(Width));
        if (FramesPerSecond is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(FramesPerSecond));
        if (PixelFormat is not ("MJPG" or "YUY2"))
            throw new ArgumentException("僅支援 MJPG 與 YUY2。", nameof(PixelFormat));
        if (new[] { Exposure, WhiteBalance, Focus }.Any(value => value.HasValue && !double.IsFinite(value.Value)))
            throw new ArgumentException("攝影機控制值必須是有限數值。");
    }
}

public sealed class CameraFrame : IDisposable
{
    private byte[]? pixels;
    private readonly ArrayPool<byte> pool;

    public CameraFrame(string cameraId, long sequence, long arrivalTimestamp, int width, int height,
        ArrayPool<byte>? pool = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        CameraId = cameraId;
        Sequence = sequence;
        ArrivalTimestamp = arrivalTimestamp;
        Width = width;
        Height = height;
        Length = checked(width * height * 3);
        this.pool = pool ?? ArrayPool<byte>.Shared;
        pixels = this.pool.Rent(Length);
    }

    public string CameraId { get; }
    public long Sequence { get; }
    public long ArrivalTimestamp { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 3;
    public int Length { get; }
    public byte[] Pixels => pixels ?? throw new ObjectDisposedException(nameof(CameraFrame));

    public void Dispose()
    {
        var released = Interlocked.Exchange(ref pixels, null);
        if (released is not null)
            pool.Return(released);
    }
}

public interface ICameraSource : IDisposable
{
    string Status { get; }
    void Open(CameraSettings settings);
    CameraFrame Read(long sequence);
}