using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MediaLens.Cloud;

/// <summary>
/// All that another computer needs to find the library backup: how to reach the storage (the rclone remotes with
/// their keys and the encryption key) and where in it the backups are. The rest (other clouds, sources, names,
/// settings) is in the backup itself.
/// </summary>
public sealed record Recovery(string Name, string BackupRoot, Dictionary<string, Dictionary<string, string>> Remotes);

/// <summary>The recovery string: the Recovery as compressed JSON in letters that survive a copy and paste.</summary>
public static class RecoveryString
{
    private const string Prefix = "MLR1.";

    public static string Encode(Recovery recovery)
    {
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize))
            deflate.Write(JsonSerializer.SerializeToUtf8Bytes(recovery));
        return Prefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Null when the text is not a recovery string (whitespace and line breaks from a paste are ignored).</summary>
    public static Recovery? Parse(string? text)
    {
        var s = new string((text ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        if (!s.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        s = s[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        s += new string('=', (4 - s.Length % 4) % 4);
        try
        {
            using var deflate = new DeflateStream(new MemoryStream(Convert.FromBase64String(s)), CompressionMode.Decompress);
            using var json = new MemoryStream();
            deflate.CopyTo(json);
            var recovery = JsonSerializer.Deserialize<Recovery>(Encoding.UTF8.GetString(json.ToArray()));
            return recovery is { Remotes.Count: > 0, BackupRoot.Length: > 0 } ? recovery : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidDataException)
        {
            return null;
        }
    }
}
