using System.Security.Cryptography;
using Dapper;
using MomentFinder.Data;

namespace MomentFinder.Library;

/// <summary>
/// Read-only sources: nothing in them is deleted, moved or added from MomentFinder, and they are not removed from the
/// library, until read-only is turned off with the password. Turned off it stays so until it is turned on again or
/// the application ends. It guards against mistakes in MomentFinder; whoever has the storage's keys can still change
/// the files with other tools.
/// </summary>
public sealed class ReadOnlyLock
{
    private const string PasswordKey = "read_only_password";
    private const int Iterations = 200_000;
    private readonly Database _db;
    private readonly HashSet<long> _open = [];

    public ReadOnlyLock(Database db) => _db = db;

    public bool HasPassword => _db.GetSetting(PasswordKey) != null;

    /// <summary>The password of all read-only sources, chosen once; only its salted hash is kept.</summary>
    public void SetPassword(string password)
    {
        if (HasPassword) throw new InvalidOperationException("The password is chosen already.");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("The password is empty.", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(16);
        _db.SetSetting(PasswordKey, $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(Hash(password, salt, Iterations))}");
    }

    public bool Check(string password)
    {
        if (_db.GetSetting(PasswordKey)?.Split('$') is not [_, var iterations, var salt, var hash]) return false;
        return CryptographicOperations.FixedTimeEquals(Hash(password, Convert.FromBase64String(salt), int.Parse(iterations)), Convert.FromBase64String(hash));
    }

    private static byte[] Hash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Read-only and not turned off now.</summary>
    public bool IsLocked(long sourceId)
    {
        lock (_open)
        {
            if (_open.Contains(sourceId)) return false;
        }
        using var c = _db.Open();
        return c.ExecuteScalar<bool>("SELECT protected FROM source WHERE id=@sourceId", new { sourceId });
    }

    /// <summary>Read-only, but turned off for now.</summary>
    public bool IsOpen(long sourceId)
    {
        lock (_open) return _open.Contains(sourceId);
    }

    /// <summary>Throws when the source is read-only now: what would change it does not start.</summary>
    public void ThrowIfLocked(long sourceId, string? name = null)
    {
        if (IsLocked(sourceId))
            throw new InvalidOperationException($"{(name is { } n ? $"\"{n}\"" : "The folder")} is read-only. Turn read-only off (with the password) to change it.");
    }

    /// <summary>Makes the source read-only (no password needed: it only makes it safer).</summary>
    public void Protect(long sourceId)
    {
        if (!HasPassword) throw new InvalidOperationException("Choose the password first.");
        using (var c = _db.Open()) c.Execute("UPDATE source SET protected=1 WHERE id=@sourceId", new { sourceId });
        lock (_open) _open.Remove(sourceId);
    }

    /// <summary>Turns read-only off until it is turned on again or the application ends; false for a wrong password.</summary>
    public bool TurnOff(long sourceId, string password)
    {
        if (!Check(password)) return false;
        lock (_open) _open.Add(sourceId);
        return true;
    }

    /// <summary>Read-only again (after it was turned off for a while).</summary>
    public void TurnOn(long sourceId)
    {
        lock (_open) _open.Remove(sourceId);
    }

    /// <summary>Not read-only any more, for good; only while it is turned off (the password was given).</summary>
    public void Unprotect(long sourceId)
    {
        if (IsLocked(sourceId)) throw new InvalidOperationException("Turn read-only off with the password first.");
        using (var c = _db.Open()) c.Execute("UPDATE source SET protected=0 WHERE id=@sourceId", new { sourceId });
        lock (_open) _open.Remove(sourceId);
    }
}
