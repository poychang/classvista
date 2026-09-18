using System.Text.Json;
using System.Text.Json.Serialization;
using ClassVista.Camera.Abstractions;

namespace ClassVista.Calibration;

public static class CalibrationProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static async Task<CalibrationProfile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(nameof(CalibrationProfile.SchemaVersion), out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion))
                throw new CalibrationProfileException(ProfileError.InvalidProfile, "Profile 缺少整數 SchemaVersion。");
            // 先讀版本，讓未來格式即使變更欄位，仍能明確回報版本不相容。
            ProfileValidator.ValidateVersion(schemaVersion);
            var profile = root.Deserialize<CalibrationProfile>(Options)
                ?? throw new CalibrationProfileException(ProfileError.InvalidProfile, "Profile 不可為 null。");
            ProfileValidator.Validate(profile);
            return profile;
        }
        catch (JsonException exception)
        {
            throw new CalibrationProfileException(ProfileError.InvalidJson, "Profile JSON 損壞、缺少必要欄位或欄位格式錯誤。", exception);
        }
    }

    public static async Task<CalibrationProfile> LoadCompatibleAsync(string path, IReadOnlyList<CameraSettings> cameras,
        string rigId, CancellationToken cancellationToken = default)
    {
        var profile = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        ProfileValidator.ValidateCompatibility(profile, cameras, rigId);
        return profile;
    }

    public static async Task SaveAsync(string path, CalibrationProfile profile, CancellationToken cancellationToken = default)
    {
        ProfileValidator.Validate(profile);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profile, Options);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            // 同目錄暫存檔完整寫入後才替換；驗證失敗或取消時保留原檔。
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (created)
                File.Delete(temporaryPath);
        }
    }
}