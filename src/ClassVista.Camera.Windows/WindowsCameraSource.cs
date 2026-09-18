using System.Diagnostics;
using System.Runtime.InteropServices;
using ClassVista.Camera.Abstractions;
using DirectShowLib;
using OpenCvSharp;

namespace ClassVista.Camera.Windows;

public static class CameraCatalog
{
    public static IReadOnlyList<CameraDevice> Enumerate()
    {
        var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        try
        {
            return devices.Select((device, index) => new CameraDevice(device.DevicePath, device.Name, index)).ToArray();
        }
        finally
        {
            foreach (var device in devices)
                device.Dispose();
        }
    }
}

public sealed class WindowsCameraSource : ICameraSource
{
    private VideoCapture? capture;
    private readonly Mat image = new();
    private CameraSettings? settings;
    public string Status { get; private set; } = "尚未開啟";

    public void Open(CameraSettings settings)
    {
        settings.Validate();
        var device = CameraCatalog.Enumerate().SingleOrDefault(device => device.Id == settings.DeviceId)
            ?? throw new IOException("找不到已指定的攝影機，請確認 USB 連線及裝置路徑。");
        this.settings = settings;
        capture?.Dispose();
        capture = new VideoCapture();
        if (!capture.Open(device.Index, VideoCaptureAPIs.DSHOW))
            throw new IOException("無法開啟攝影機；請檢查權限或其他程式是否占用。");

        var warnings = new List<string>();
        Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC(settings.PixelFormat), "輸出格式");
        Set(VideoCaptureProperties.FrameWidth, settings.Width, "寬度");
        Set(VideoCaptureProperties.FrameHeight, settings.Height, "高度");
        Set(VideoCaptureProperties.Fps, settings.FramesPerSecond, "FPS");
        if (settings.Exposure is { } exposure)
        {
            Set(VideoCaptureProperties.AutoExposure, 0, "關閉自動曝光");
            Set(VideoCaptureProperties.Exposure, exposure, "曝光");
        }
        if (settings.WhiteBalance is { } whiteBalance)
        {
            Set(VideoCaptureProperties.AutoWB, 0, "關閉自動白平衡");
            Set(VideoCaptureProperties.WBTemperature, whiteBalance, "白平衡");
        }
        if (settings.Focus is { } focus)
        {
            Set(VideoCaptureProperties.AutoFocus, 0, "關閉自動對焦");
            Set(VideoCaptureProperties.Focus, focus, "焦距");
        }
        var actualFps = capture.Get(VideoCaptureProperties.Fps);
        var actualFormat = (int)capture.Get(VideoCaptureProperties.FourCC);
        var format = new string(Enumerable.Range(0, 4).Select(offset => (char)((actualFormat >> (8 * offset)) & 255)).ToArray());
        if (actualFps <= 0 || Math.Abs(actualFps - settings.FramesPerSecond) > 1)
            warnings.Add($"驅動回報 FPS={actualFps:F1}，未確認目標幀率");
        if (actualFormat != VideoWriter.FourCC(settings.PixelFormat))
            warnings.Add("驅動回報格式與要求不同");
        Status = $"DirectShow / {format} / 驅動 FPS {actualFps:F1}";
        if (warnings.Count > 0)
            Status += "；警告：" + string.Join("、", warnings);

        void Set(VideoCaptureProperties property, double value, string name)
        {
            if (!capture.Set(property, value))
                warnings.Add($"{name}設定未被驅動接受");
        }
    }

    public CameraFrame Read(long sequence)
    {
        if (capture is null || settings is null)
            throw new InvalidOperationException("攝影機尚未開啟。");
        if (!capture.Read(image) || image.Empty())
            throw new IOException("無法讀取影格，將重新連線。");
        // 此時間只代表主機讀取完成，不代表攝影機感光時間。
        var arrival = Stopwatch.GetTimestamp();
        if (image.Width != settings.Width || image.Height != settings.Height || image.Type() != MatType.CV_8UC3)
            throw new IOException($"實際格式 {image.Width}x{image.Height}/{image.Type()} 不符合設定。");
        var frame = new CameraFrame(settings.DeviceId, sequence, arrival, image.Width, image.Height);
        try
        {
            if (image.IsContinuous())
                Marshal.Copy(image.Data, frame.Pixels, 0, frame.Length);
            else
            {
                var height = image.Height;
                for (var row = 0; row < height; row++)
                    Marshal.Copy(image.Ptr(row), frame.Pixels, row * frame.Stride, frame.Stride);
            }
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        capture?.Dispose();
        capture = null;
        image.Dispose();
    }
}