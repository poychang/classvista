using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassVista.App;
using Xunit;

namespace ClassVista.Tests;

public sealed class PreviewTests
{
    [Fact]
    public async Task DesktopPreviewRendersMovingFramesPersistsSettingsAndStops()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory = Path.Combine(Path.GetTempPath(), $"classvista-ui-{Guid.NewGuid():N}");
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var application = new ClassVista.App.App();
                application.InitializeComponent();
                window = new MainWindow(directory);
                window.Show();
                RunAsync();
                Dispatcher.Run();

                async void RunAsync()
                {
                    try
                    {
                        var start = (Button)window.FindName("StartButton");
                        var stop = (Button)window.FindName("StopButton");
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        while (!start.IsEnabled)
                            await Task.Delay(20, timeout.Token);
                        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        while (!stop.IsEnabled)
                            await Task.Delay(20, timeout.Token);
                        await Task.Delay(1500, timeout.Token);
                        var images = Descendants<Image>(window).ToArray();
                        Assert.Equal(2, images.Length);
                        var before = images.Select(ReadPixels).ToArray();
                        SaveScreenshot(window, "preview-desktop.png");
                        window.Width = 960;
                        window.Height = 720;
                        await Task.Delay(1000, timeout.Token);
                        for (var index = 0; index < images.Length; index++)
                        {
                            var after = ReadPixels(images[index]);
                            Assert.False(before[index].SequenceEqual(after));
                            Assert.True(after.Distinct().Count() > 8);
                        }
                        SaveScreenshot(window, "preview-compact.png");
                        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        while (!start.IsEnabled)
                            await Task.Delay(20, timeout.Token);
                        Assert.All(images, image => Assert.Null(image.Source));
                        Assert.True(File.Exists(Path.Combine(directory, "cameras.json")));
                        var report = Assert.Single(Directory.GetFiles(Path.Combine(directory, "Reports"), "*.jsonl"));
                        using var final = JsonDocument.Parse(File.ReadLines(report).Last());
                        Assert.Equal("final", final.RootElement.GetProperty("Type").GetString());
                        foreach (var camera in final.RootElement.GetProperty("Cameras").EnumerateArray())
                        {
                            Assert.True(camera.GetProperty("IsSimulation").GetBoolean());
                            Assert.Equal(0, camera.GetProperty("Metrics").GetProperty("ReadFailures").GetInt64());
                            Assert.True(camera.GetProperty("Metrics").GetProperty("Frames").GetInt64() > 10);
                        }
                        var artifactDirectory = Path.Combine(FindRepository(), "artifacts", "validation");
                        File.Copy(report, Path.Combine(artifactDirectory, "preview-smoke.jsonl"), true);
                        completion.SetResult();
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                    }
                    finally
                    {
                        window.Close();
                        application.Shutdown();
                    }
                }
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static byte[] ReadPixels(Image image)
    {
        var bitmap = Assert.IsType<WriteableBitmap>(image.Source);
        Assert.Equal(1920, bitmap.PixelWidth);
        Assert.Equal(1080, bitmap.PixelHeight);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 3];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 3, 0);
        return pixels;
    }

    private static IEnumerable<TElement> Descendants<TElement>(DependencyObject parent) where TElement : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TElement element)
                yield return element;
            foreach (var descendant in Descendants<TElement>(child))
                yield return descendant;
        }
    }

    private static void SaveScreenshot(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(FindRepository(), "artifacts", "validation");
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static string FindRepository()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ClassVista.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("找不到 ClassVista.sln。");
    }
}