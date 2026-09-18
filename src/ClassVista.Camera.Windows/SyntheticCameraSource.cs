using System.Diagnostics;
using ClassVista.Camera.Abstractions;
using OpenCvSharp;

namespace ClassVista.Camera.Windows;

public sealed class SyntheticCameraSource : ICameraSource
{
    private Mat? image;
    private CameraSettings? settings;
    public string Status => "模擬來源 / 非硬體驗收資料";

    public void Open(CameraSettings settings)
    {
        settings.Validate();
        this.settings = settings;
        image = new Mat(settings.Height, settings.Width, MatType.CV_8UC3);
    }

    public CameraFrame Read(long sequence)
    {
        if (settings is null || image is null)
            throw new InvalidOperationException("模擬來源尚未開啟。");
        image.SetTo(new Scalar(38, 40, 36));
        var colors = new[] { new Scalar(105, 176, 38), new Scalar(67, 151, 215), new Scalar(189, 112, 49) };
        for (var index = 0; index < colors.Length; index++)
        {
            var start = index * settings.Width / colors.Length;
            var end = (index + 1) * settings.Width / colors.Length;
            Cv2.Rectangle(image, new Rect(start, 0, end - start, settings.Height / 3), colors[index], -1);
        }
        var position = (int)((sequence * 12) % settings.Width);
        Cv2.Line(image, new Point(position, 0), new Point(position, settings.Height - 1), Scalar.White, 4);
        Cv2.PutText(image, $"{settings.DeviceId} / frame {sequence}", new Point(24, settings.Height / 2),
            HersheyFonts.HersheySimplex, 1, Scalar.White, 2);
        var frame = new CameraFrame(settings.DeviceId, sequence, Stopwatch.GetTimestamp(), settings.Width, settings.Height);
        System.Runtime.InteropServices.Marshal.Copy(image.Data, frame.Pixels, 0, frame.Length);
        return frame;
    }

    public void Dispose() => image?.Dispose();
}