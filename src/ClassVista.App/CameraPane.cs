using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassVista.Camera.Abstractions;

namespace ClassVista.App;

internal sealed class CameraPane
{
    private readonly ComboBox devices = new() { DisplayMemberPath = nameof(CameraDevice.Name), SelectedValuePath = nameof(CameraDevice.Id) };
    private readonly ComboBox format = new() { ItemsSource = new[] { "MJPG", "YUY2" }, SelectedIndex = 0, Width = 90 };
    private readonly TextBox exposure = new();
    private readonly TextBox whiteBalance = new();
    private readonly TextBox focus = new();
    private readonly StackPanel settings = new();
    private readonly Image preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock empty = new() { Text = "尚無影像", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { Text = "待命", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) };
    private readonly TextBlock diagnostics = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 64, Foreground = Brushes.DarkSlateGray };
    private WriteableBitmap? bitmap;
    private CameraSettings saved;

    public CameraPane(string title, CameraSettings initial)
    {
        saved = initial;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(140) });
        settings.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        devices.Margin = new Thickness(0, 0, 0, 10);
        settings.Children.Add(devices);
        var formatRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        formatRow.Children.Add(format);
        formatRow.Children.Add(new TextBlock { Text = "1920 × 1080 / 30 FPS", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        settings.Children.Add(formatRow);
        var controls = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        var fields = new[] { ("曝光", exposure), ("白平衡 (K)", whiteBalance), ("焦距", focus) };
        for (var index = 0; index < fields.Length; index++)
        {
            controls.ColumnDefinitions.Add(new ColumnDefinition());
            var field = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            field.Children.Add(new TextBlock { Text = fields[index].Item1, Margin = new Thickness(0, 0, 0, 4) });
            fields[index].Item2.ToolTip = "留空不更動；數值單位及範圍依攝影機驅動";
            field.Children.Add(fields[index].Item2);
            Grid.SetColumn(field, index);
            controls.Children.Add(field);
        }
        settings.Children.Add(controls);
        grid.Children.Add(settings);
        var imageHost = new Grid { Background = new SolidColorBrush(Color.FromRgb(25, 29, 27)), ClipToBounds = true };
        imageHost.Children.Add(preview);
        imageHost.Children.Add(empty);
        Grid.SetRow(imageHost, 1);
        grid.Children.Add(imageHost);
        var info = new StackPanel();
        info.Children.Add(status);
        info.Children.Add(diagnostics);
        var infoHost = new ScrollViewer { Content = info, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(infoHost, 2);
        grid.Children.Add(infoHost);
        Root = grid;
        format.SelectedItem = initial.PixelFormat;
        exposure.Text = initial.Exposure?.ToString(CultureInfo.InvariantCulture) ?? "";
        whiteBalance.Text = initial.WhiteBalance?.ToString(CultureInfo.InvariantCulture) ?? "";
        focus.Text = initial.Focus?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    public FrameworkElement Root { get; }

    public void SetDevices(IReadOnlyList<CameraDevice> available)
    {
        var selected = devices.SelectedValue as string ?? saved.DeviceId;
        var choices = available.ToList();
        if (choices.All(device => device.Id != selected))
            choices.Add(new CameraDevice(selected, "離線：" + selected, -1));
        devices.ItemsSource = choices;
        devices.SelectedValue = selected;
    }

    public CameraSettings ReadSettings()
    {
        saved = new CameraSettings(devices.SelectedValue as string ?? "", PixelFormat: (string)format.SelectedItem,
            Exposure: Parse(exposure.Text), WhiteBalance: Parse(whiteBalance.Text), Focus: Parse(focus.Text));
        saved.Validate();
        return saved;

        static double? Parse(string text) => string.IsNullOrWhiteSpace(text) ? null : double.Parse(text, CultureInfo.InvariantCulture);
    }

    public void SetRunning(bool running) => settings.IsEnabled = !running;

    public void Present(CameraFrame frame)
    {
        if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
            preview.Source = bitmap;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
        empty.Visibility = Visibility.Collapsed;
    }

    public void Clear(string message)
    {
        preview.Source = null;
        bitmap = null;
        empty.Text = message;
        empty.Visibility = Visibility.Visible;
    }

    public void UpdateStatus(string message, string detail, bool showDiagnostics)
    {
        status.Text = message;
        diagnostics.Text = detail;
        diagnostics.Visibility = showDiagnostics ? Visibility.Visible : Visibility.Hidden;
    }
}