using ClassVista.Camera.Abstractions;

namespace ClassVista.Calibration;

public static class ProfileValidator
{
    public static void Validate(CalibrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateVersion(profile.SchemaVersion);
        Require(!string.IsNullOrWhiteSpace(profile.ProfileId), "ProfileId 不可空白。");
        Require(!string.IsNullOrWhiteSpace(profile.RigId), "RigId 不可空白。");
        Require(profile.CreatedUtc != default && profile.CreatedUtc.Offset == TimeSpan.Zero, "CreatedUtc 必須是有效的 UTC 時間。");
        Require(profile.Projection == "planar", "目前只支援 planar Profile；其他投影格式尚未定義。");
        Require(profile.PanoramaWidth > 0 && profile.PanoramaHeight > 0, "全景尺寸必須大於零。");
        var region = profile.ValidRegion;
        Require(region is not null, "缺少 ValidRegion。");
        Require(region!.X >= 0 && region.Y >= 0 && region.Width > 0 && region.Height > 0 &&
            (long)region.X + region.Width <= profile.PanoramaWidth &&
            (long)region.Y + region.Height <= profile.PanoramaHeight, "ValidRegion 必須位於全景範圍內。");
        Require(profile.Cameras is { Length: > 0 }, "至少需要一筆 Camera 校正資料。");
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var camera in profile.Cameras!)
        {
            Require(camera is not null, "Camera 校正資料不可為 null。");
            Require(!string.IsNullOrWhiteSpace(camera!.DeviceId) && identifiers.Add(camera.DeviceId), "Camera DeviceId 不可空白或重複。");
            Require(camera.Width is > 0 and <= 7680 && camera.Height is > 0 and <= 4320,
                $"Camera {camera.DeviceId} 的解析度不在支援範圍內。");
            Require(IsFiniteArray(camera.IntrinsicMatrix, 9), $"Camera {camera.DeviceId} 的內參必須是 9 個有限數值。");
            var intrinsic = camera.IntrinsicMatrix;
            Require(intrinsic[0] > 0 && intrinsic[4] > 0 && intrinsic[3] == 0 &&
                intrinsic[6] == 0 && intrinsic[7] == 0 && intrinsic[8] == 1,
                $"Camera {camera.DeviceId} 的內參必須具有正焦距及標準齊次格式。");
            Require(camera.DistortionCoefficients is { Length: 4 or 5 or 8 or 12 or 14 } &&
                camera.DistortionCoefficients.All(double.IsFinite),
                $"Camera {camera.DeviceId} 的 OpenCV 針孔畸變係數長度須為 4、5、8、12 或 14，且數值有限。");
            Require(IsFiniteArray(camera.Homography, 9), $"Camera {camera.DeviceId} 的 Homography 必須是 9 個有限數值。");
            Require(IsInvertible(camera.Homography), $"Camera {camera.DeviceId} 的 Homography 不可退化。");
        }
    }

    public static void ValidateCompatibility(CalibrationProfile profile, IReadOnlyList<CameraSettings> cameras, string rigId)
    {
        Validate(profile);
        ArgumentNullException.ThrowIfNull(cameras);
        if (!string.Equals(profile.RigId, rigId, StringComparison.Ordinal))
            throw Incompatible("支架識別已變更，請重新校正。");
        if (cameras.Count != profile.Cameras.Length)
            throw Incompatible("Camera 數量與 Profile 不符，請重新校正。");
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var camera in cameras)
        {
            if (camera is null || string.IsNullOrWhiteSpace(camera.DeviceId) || !identifiers.Add(camera.DeviceId))
                throw Incompatible("目前 Camera 識別不可空白或重複。");
            var calibrated = Array.Find(profile.Cameras, entry => string.Equals(entry.DeviceId, camera.DeviceId, StringComparison.Ordinal));
            if (calibrated is null)
                throw Incompatible($"Camera {camera.DeviceId} 不在 Profile 中，請重新指定或重新校正。");
            if (calibrated.Width != camera.Width || calibrated.Height != camera.Height)
                throw Incompatible($"Camera {camera.DeviceId} 解析度不符：Profile {calibrated.Width}x{calibrated.Height}，目前 {camera.Width}x{camera.Height}；請重新校正。");
        }
    }

    internal static void ValidateVersion(int version)
    {
        if (version != CalibrationProfile.CurrentSchemaVersion)
            throw new CalibrationProfileException(ProfileError.UnsupportedVersion,
                $"不支援 Profile 版本 {version}；目前僅支援版本 {CalibrationProfile.CurrentSchemaVersion}，請使用相容版本或重新校正。");
    }

    private static bool IsFiniteArray(double[]? values, int length) => values?.Length == length && values.All(double.IsFinite);

    private static bool IsInvertible(double[] matrix)
    {
        // 齊次矩陣可等比例縮放；正規化後再判斷，避免行列式溢位。
        var scale = matrix.Max(value => Math.Abs(value));
        if (scale == 0)
            return false;
        var normalized = matrix.Select(value => value / scale).ToArray();
        var determinant = normalized[0] * (normalized[4] * normalized[8] - normalized[5] * normalized[7])
            - normalized[1] * (normalized[3] * normalized[8] - normalized[5] * normalized[6])
            + normalized[2] * (normalized[3] * normalized[7] - normalized[4] * normalized[6]);
        return double.IsFinite(determinant) && determinant != 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new CalibrationProfileException(ProfileError.InvalidProfile, message);
    }

    private static CalibrationProfileException Incompatible(string message) => new(ProfileError.IncompatibleSetup, message);
}