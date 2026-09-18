using System.IO;
using System.Text.Json;
using ClassVista.Calibration;
using ClassVista.Camera.Abstractions;
using Xunit;

namespace ClassVista.Tests;

public sealed class CalibrationProfileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"classvista-profile-{Guid.NewGuid():N}");
    private string ProfilePath => Path.Combine(directory, "profile.json");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RoundTripPreservesAllFieldsAndMatchesDevicesRegardlessOfOrder(int count)
    {
        var profile = CreateProfile(count);
        await CalibrationProfileStore.SaveAsync(ProfilePath, profile);
        var cameras = profile.Cameras.Reverse().Select(camera => new CameraSettings(camera.DeviceId, camera.Width, camera.Height)).ToArray();
        var loaded = await CalibrationProfileStore.LoadCompatibleAsync(ProfilePath, cameras, profile.RigId);
        Assert.Equal(JsonSerializer.Serialize(profile), JsonSerializer.Serialize(loaded));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task UnsupportedVersionIsReportedBeforeFutureFieldsAreParsed(int version)
    {
        await WriteJsonAsync(JsonSerializer.Serialize(new { SchemaVersion = version, FutureField = "unknown" }));
        var error = await Assert.ThrowsAsync<CalibrationProfileException>(() => CalibrationProfileStore.LoadAsync(ProfilePath));
        Assert.Equal(ProfileError.UnsupportedVersion, error.Error);
        Assert.Contains($"版本 {version}", error.Message);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"SchemaVersion\":1}")]
    [InlineData("{\"SchemaVersion\":1,\"SchemaVersion\":1}")]
    public async Task MalformedOrIncompleteJsonIsRejected(string json)
    {
        await WriteJsonAsync(json);
        var error = await Assert.ThrowsAsync<CalibrationProfileException>(() => CalibrationProfileStore.LoadAsync(ProfilePath));
        Assert.Equal(ProfileError.InvalidJson, error.Error);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":\"1\"}")]
    public async Task MissingVersionCannotDefaultToCurrentVersion(string json)
    {
        await WriteJsonAsync(json);
        var error = await Assert.ThrowsAsync<CalibrationProfileException>(() => CalibrationProfileStore.LoadAsync(ProfilePath));
        Assert.Equal(ProfileError.InvalidProfile, error.Error);
    }

    [Fact]
    public void CompatibilityRejectsChangedRigCountDevicesAndResolution()
    {
        var profile = CreateProfile();
        var cameras = profile.Cameras.Select(camera => new CameraSettings(camera.DeviceId)).ToArray();
        AssertMismatch(cameras, "other-rig", "支架");
        AssertMismatch(cameras[..1], profile.RigId, "數量");
        AssertMismatch([cameras[0], cameras[1] with { DeviceId = "unknown" }], profile.RigId, "不在 Profile");
        AssertMismatch([cameras[0], cameras[0]], profile.RigId, "重複");
        AssertMismatch([cameras[0], cameras[1] with { Width = 1280 }], profile.RigId, "解析度");
        AssertMismatch([cameras[0], cameras[1] with { Height = 720 }], profile.RigId, "解析度");

        void AssertMismatch(CameraSettings[] current, string rigId, string message)
        {
            var error = Assert.Throws<CalibrationProfileException>(() => ProfileValidator.ValidateCompatibility(profile, current, rigId));
            Assert.Equal(ProfileError.IncompatibleSetup, error.Error);
            Assert.Contains(message, error.Message);
        }
    }

    [Fact]
    public void InvalidProfileFieldsAndMatricesAreRejected()
    {
        var profile = CreateProfile();
        var invalidProfiles = new[]
        {
            profile with { ProfileId = " " }, profile with { RigId = "" }, profile with { CreatedUtc = default },
            profile with { Projection = "cylindrical" }, profile with { PanoramaWidth = 0 },
            profile with { ValidRegion = null! }, profile with { ValidRegion = new WarpRegion(-1, 0, 10, 10) },
            profile with { ValidRegion = new WarpRegion(int.MaxValue, 0, 20, 20) },
            profile with { Cameras = null! }, profile with { Cameras = [] }, profile with { Cameras = [null!] },
            profile with { Cameras = [profile.Cameras[0], profile.Cameras[0]] }
        };
        foreach (var invalid in invalidProfiles)
            AssertInvalid(invalid);
        var camera = profile.Cameras[0];
        var invalidCameras = new[]
        {
            camera with { DeviceId = "" }, camera with { Width = 0 }, camera with { Height = 5000 },
            camera with { IntrinsicMatrix = null! }, camera with { IntrinsicMatrix = [1, 2] },
            camera with { IntrinsicMatrix = [double.NaN, 0, 960, 0, 1000, 540, 0, 0, 1] },
            camera with { IntrinsicMatrix = [-1, 0, 960, 0, 1000, 540, 0, 0, 1] },
            camera with { IntrinsicMatrix = [1000, 0, 960, 0, 1000, 540, 0, 1, 1] },
            camera with { DistortionCoefficients = null! }, camera with { DistortionCoefficients = [0, 0, 0] },
            camera with { DistortionCoefficients = [0, 0, 0, double.PositiveInfinity] },
            camera with { Homography = null! }, camera with { Homography = [1, 2] },
            camera with { Homography = new double[9] },
            camera with { Homography = [1, 2, 3, 1, 2, 3, 0, 0, 1] }
        };
        foreach (var invalid in invalidCameras)
            AssertInvalid(profile with { Cameras = [invalid] });

        static void AssertInvalid(CalibrationProfile invalid)
        {
            var error = Assert.Throws<CalibrationProfileException>(() => ProfileValidator.Validate(invalid));
            Assert.Equal(ProfileError.InvalidProfile, error.Error);
        }
    }

    [Fact]
    public async Task LoadAlsoValidatesProfileStructureAndCompatibility()
    {
        var json = JsonSerializer.SerializeToNode(CreateProfile())!;
        json["Cameras"]![0]!["Width"] = 0;
        await WriteJsonAsync(json.ToJsonString());
        var error = await Assert.ThrowsAsync<CalibrationProfileException>(() => CalibrationProfileStore.LoadAsync(ProfilePath));
        Assert.Equal(ProfileError.InvalidProfile, error.Error);
        await CalibrationProfileStore.SaveAsync(ProfilePath, CreateProfile());
        error = await Assert.ThrowsAsync<CalibrationProfileException>(() => CalibrationProfileStore.LoadCompatibleAsync(ProfilePath, [], "rig-v1"));
        Assert.Equal(ProfileError.IncompatibleSetup, error.Error);
    }

    [Fact]
    public async Task FailedOrCanceledSavePreservesPreviousFileAndLeavesNoTemporaryFiles()
    {
        var profile = CreateProfile();
        await CalibrationProfileStore.SaveAsync(ProfilePath, profile);
        var original = await File.ReadAllBytesAsync(ProfilePath);
        await Assert.ThrowsAsync<CalibrationProfileException>(() => CalibrationProfileStore.SaveAsync(ProfilePath, profile with { Cameras = [] }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CalibrationProfileStore.SaveAsync(ProfilePath, profile, cancellation.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(ProfilePath));
        Assert.Single(Directory.GetFiles(directory));
        await CalibrationProfileStore.SaveAsync(ProfilePath, profile with { ProfileId = "replacement" });
        Assert.Equal("replacement", (await CalibrationProfileStore.LoadAsync(ProfilePath)).ProfileId);
    }

    [Fact]
    public async Task FailedReplacementCleansTemporaryFile()
    {
        Directory.CreateDirectory(ProfilePath);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CalibrationProfileStore.SaveAsync(ProfilePath, CreateProfile()));
        Assert.Empty(Directory.GetFiles(directory));
        Assert.True(Directory.Exists(ProfilePath));
    }

    [Fact]
    public async Task MissingRegionCoordinateAndUnknownFieldsAreRejectedWithoutRewritingFile()
    {
        var json = JsonSerializer.SerializeToNode(CreateProfile())!;
        json["ValidRegion"]!.AsObject().Remove("X");
        await AssertRejectedAsync(json.ToJsonString());
        json = JsonSerializer.SerializeToNode(CreateProfile())!;
        json["UnexpectedField"] = true;
        await AssertRejectedAsync(json.ToJsonString());

        async Task AssertRejectedAsync(string content)
        {
            await WriteJsonAsync(content);
            var error = await Assert.ThrowsAsync<CalibrationProfileException>(() => CalibrationProfileStore.LoadAsync(ProfilePath));
            Assert.Equal(ProfileError.InvalidJson, error.Error);
            Assert.Equal(content, await File.ReadAllTextAsync(ProfilePath));
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(14)]
    public void SupportedPinholeDistortionLengthsAndScaledHomographyAreAccepted(int length)
    {
        var profile = CreateProfile(1);
        var camera = profile.Cameras[0] with
        {
            DistortionCoefficients = new double[length],
            Homography = profile.Cameras[0].Homography.Select(value => value * 1e100).ToArray()
        };
        ProfileValidator.Validate(profile with { Cameras = [camera] });
    }

    [Fact]
    public async Task MissingFileIsNotSilentlyReplacedWithDefaults()
    {
        Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<FileNotFoundException>(() => CalibrationProfileStore.LoadAsync(ProfilePath));
        Assert.Empty(Directory.GetFiles(directory));
    }

    private async Task WriteJsonAsync(string json)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(ProfilePath, json);
    }

    private static CalibrationProfile CreateProfile(int count = 2) => new()
    {
        SchemaVersion = CalibrationProfile.CurrentSchemaVersion,
        ProfileId = "offline-fixture",
        CreatedUtc = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero),
        RigId = "rig-v1",
        Projection = "planar",
        PanoramaWidth = 1920 + (count - 1) * 1440,
        PanoramaHeight = 1080,
        ValidRegion = new WarpRegion(0, 0, 1920 + (count - 1) * 1440, 1080),
        Cameras = Enumerable.Range(0, count).Select(index => new CameraCalibration
        {
            DeviceId = $"offline:{index}", Width = 1920, Height = 1080,
            IntrinsicMatrix = [1000, 0, 960, 0, 1000, 540, 0, 0, 1],
            DistortionCoefficients = [0.01, -0.001, 0, 0, 0],
            Homography = [1, 0, index * 1440, 0, 1, 0, 0, 0, 1]
        }).ToArray()
    };

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}