using System.Text.Json;

namespace MomentFinder.Cloud;

/// <summary>
/// This computer, for the cloud: its mirrors go under a folder of its name and its library backup into a folder of
/// its own, so two computers on one cloud never write over each other. Kept beside the library, not in it (the
/// library can be restored from another computer).
/// </summary>
public sealed record DeviceInfo(string Id, string Name)
{
    /// <summary>The folder of this computer's library backup: the name people know plus the id that tells two apart.</summary>
    public string Folder => $"{Safe(Name)}-{Id}";

    public string MirrorFolder => Safe(Name);

    public static string Safe(string name) =>
        string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' or ':' ? '_' : ch)).Trim();

    public static DeviceInfo Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "device.json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<DeviceInfo>(File.ReadAllText(path)) is { Id.Length: > 0 } known) return known;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
        }
        var device = new DeviceInfo(Guid.NewGuid().ToString("N")[..6], Environment.MachineName);
        File.WriteAllText(path, JsonSerializer.Serialize(device));
        return device;
    }
}
