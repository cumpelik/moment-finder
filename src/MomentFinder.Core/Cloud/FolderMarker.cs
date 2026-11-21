using System.Text.Json;

namespace MomentFinder.Cloud;

/// <summary>
/// A small file in a folder of the cloud that says whose it is, so every computer of the library treats it the same:
/// the backup of one computer's folder (that computer decides what is in it, the others only look), or a folder
/// that lives in the cloud (everybody may change it). Its name starts with a dot: scans and copies skip it.
/// </summary>
public sealed record FolderMarker(string Mode, string Home, string HomeName, string Name)
{
    public const string FileName = ".medialens-folder.json"; // the application's first name, kept for existing storages
    public const string Backup = "backup";
    public const string Shared = "shared";

    public bool IsBackup => Mode == Backup;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>The marker of a folder; null when there is none (or it cannot be read).</summary>
    public static async Task<FolderMarker?> ReadAsync(Rclone rclone, string folder, CancellationToken ct)
    {
        var text = await rclone.ReadTextAsync(CloudService.Join(folder, FileName), ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonSerializer.Deserialize<FolderMarker>(text, Json) is { Mode.Length: > 0 } marker ? marker : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task WriteAsync(Rclone rclone, string folder, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "momentfinder-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(this, Json), ct);
        try
        {
            await rclone.UploadAsync(tmp, CloudService.Join(folder, FileName), ct);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
