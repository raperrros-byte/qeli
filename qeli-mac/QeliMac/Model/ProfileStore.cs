using System.IO;
using System.Text;
using System.Text.Json;
using Qeli.Shared.Model;

namespace QeliMac.Model;

/// <summary>Persists the profile list to ~/Library/Application Support/Qeli/profiles.json,
/// encrypted at rest with AES-256-GCM. Profiles carry the server password and
/// obfs_key, so they must not sit in plaintext; the AES key comes from the macOS
/// Keychain (see <see cref="SecureKey"/>). A legacy plaintext file (pre-E1) is read
/// transparently and re-written into a versioned authenticated envelope.
/// See docs/*/archive/plans/RELEASE-FIXES.md E1.</summary>
public static class ProfileStore
{
    private static readonly string Dir = Paths.UserDir;
    private static readonly string FilePath = Path.Combine(Dir, "profiles.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<VpnConfig> Load()
    {
        // Absent file = normal first run. Only a PRESENT-but-unreadable file is dangerous.
        if (!File.Exists(FilePath)) return new List<VpnConfig>();
        try
        {
            var raw = File.ReadAllBytes(FilePath);
            var plaintext = EncryptedEnvelope.Open(
                raw, SecureKey.GetOrCreate(), allowLegacyArray: true, out bool needsMigration);
            string json = Encoding.UTF8.GetString(plaintext);
            var profiles = JsonSerializer.Deserialize<List<VpnConfig>>(json, Options) ?? new List<VpnConfig>();
            // Profiles saved before the stable-Id fix have no "Id" field; the deserializer
            // left each at a fresh-GUID default that would otherwise change on every load
            // (settings reference profiles by Id). Persist once to freeze those Ids.
            bool needsIdMigration = profiles.Count > 0 && !json.Contains("\"Id\":");
            if (needsMigration || needsIdMigration) Save(profiles);
            return profiles;
        }
        catch (Exception ex)
        {
            // The file exists but couldn't be decrypted/parsed (e.g. Keychain key lost).
            // Do NOT silently return an empty list — the next Save would overwrite the
            // (possibly recoverable) file. Preserve it aside first, then start empty.
            try { File.Move(FilePath, FilePath + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
            catch { /* best effort */ }
            System.Diagnostics.Debug.WriteLine($"ProfileStore: profiles.json unreadable, preserved aside ({ex.Message})");

            // File.Replace keeps one last authenticated generation. Recover it automatically
            // instead of opening with an empty profile list after a torn/corrupt latest write.
            try
            {
                var backup = FilePath + ".bak";
                if (File.Exists(backup))
                {
                    var plaintext = EncryptedEnvelope.Open(
                        File.ReadAllBytes(backup), SecureKey.GetOrCreate(), true, out _);
                    var profiles = JsonSerializer.Deserialize<List<VpnConfig>>(
                        Encoding.UTF8.GetString(plaintext), Options) ?? new List<VpnConfig>();
                    Save(profiles);
                    System.Diagnostics.Debug.WriteLine("ProfileStore: restored profiles from authenticated .bak");
                    return profiles;
                }
            }
            catch (Exception backupError)
            {
                System.Diagnostics.Debug.WriteLine($"ProfileStore: .bak recovery failed ({backupError.Message})");
            }
            return new List<VpnConfig>();
        }
    }

    public static void Save(IEnumerable<VpnConfig> profiles)
    {
        Directory.CreateDirectory(Dir);
        var key = SecureKey.GetOrCreate();
        var pt = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profiles, Options));
        var blob = EncryptedEnvelope.Seal(pt, key);
        // Atomic write (temp born 0600 + replace): a crash mid-write must not truncate the
        // only copy, and the secret ciphertext must never briefly be world-readable.
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, blob);
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        if (File.Exists(FilePath))
            File.Replace(tmp, FilePath, FilePath + ".bak");
        else
            File.Move(tmp, FilePath);
    }
}
