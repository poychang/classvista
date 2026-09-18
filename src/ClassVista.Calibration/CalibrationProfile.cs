using System.Text.Json.Serialization;

namespace ClassVista.Calibration;

public sealed record WarpRegion(
    [property: JsonRequired] int X,
    [property: JsonRequired] int Y,
    [property: JsonRequired] int Width,
    [property: JsonRequired] int Height);

public sealed record CameraCalibration
{
    public required string DeviceId { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    // 矩陣皆採逐列排列；內參單位為輸入影像像素。
    public required double[] IntrinsicMatrix { get; init; }
    public required double[] DistortionCoefficients { get; init; }
    // 將去畸變後、沿用原內參的像素座標映射到全景座標。
    public required double[] Homography { get; init; }
}

public sealed record CalibrationProfile
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required string ProfileId { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string RigId { get; init; }
    public required string Projection { get; init; }
    public required int PanoramaWidth { get; init; }
    public required int PanoramaHeight { get; init; }
    public required WarpRegion ValidRegion { get; init; }
    public required CameraCalibration[] Cameras { get; init; }
}

public enum ProfileError
{
    InvalidJson,
    UnsupportedVersion,
    InvalidProfile,
    IncompatibleSetup
}

public sealed class CalibrationProfileException(ProfileError error, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ProfileError Error { get; } = error;
}