using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GamePingBooster.App.Services;

/// <summary>
/// The refresh token, kept so signing in survives closing the app.
///
/// DPAPI at CURRENT USER scope, in %LOCALAPPDATA%, and both halves of that differ from everything
/// the service stores:
///
///   device key, licence token   the MACHINE's, wrapped at machine scope, in %ProgramData%,
///                               written by a LocalSystem service
///   refresh token (here)        the PERSON's, wrapped at user scope, in their own profile,
///                               written by a normal-user process
///
/// User scope is the point, not an accident. Another account on the same PC signing in should
/// get its own licence, not inherit this one; machine scope would hand it to them. It also means
/// the file is useless if copied to another machine or another account, which is the same
/// property the device key relies on.
///
/// An unreadable file is deleted without ceremony. A refresh token is replaceable by signing in
/// again - keeping a corrupt one, or refusing to start over it, would turn a self-healing
/// situation into a support case. The device key is the opposite and is treated the opposite way.
/// </summary>
public static class RefreshTokenStore
{
    private const string FileName = "refresh";

    /// <summary>See DeviceIdentity.Entropy: a domain separator, not a secret.</summary>
    private static readonly byte[] Entropy = "GamePingBooster.RefreshToken.v1"u8.ToArray();

    private static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GamePingBooster");

    private static string FilePath => Path.Combine(DirectoryPath, FileName);

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var blob = File.ReadAllBytes(FilePath);
            var raw = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            var text = Encoding.UTF8.GetString(raw);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception)
        {
            Clear();
            return null;
        }
    }

    public static void Save(string refreshToken)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken),
                Entropy, DataProtectionScope.CurrentUser);

            // Temporary file then replace, for the same reason the service does it: a half
            // written credential is one more sign-in the user did not expect to do.
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // Not fatal. Sign-in still works for this run; it just will not be remembered.
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception)
        {
            // Nothing useful to do, and nothing depends on it having worked.
        }
    }
}
