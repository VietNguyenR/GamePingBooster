using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GamePingBooster.Service;

/// <summary>
/// A random 64-bit value, generated once per installation and kept on disk.
///
/// Its only purpose is to let the relay hand a reconnecting client the same inner address it had
/// before, so a two-second network drop does not force the virtual adapter to be re-addressed and
/// every route reinstalled. Without it, a reconnect means a new address and a full rebuild of the
/// routing table - during the exact moment the network is already unreliable.
///
/// It is not an account, not a licence key, and carries nothing about the machine or the user:
/// it comes straight from the OS random source and is meaningless outside this relay's session
/// table. If the file is lost the client simply gets a new address on its next connect.
/// </summary>
internal static class ClientIdentity
{
    private const string FileName = "client-id";

    public static ulong Load()
    {
        var path = Path.Combine(ServiceConfig.DefaultDirectory, FileName);

        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == 8)
                {
                    var value = BinaryPrimitives.ReadUInt64BigEndian(existing);
                    if (value != 0) return value;
                }
            }
        }
        catch (IOException)
        {
            // Unreadable for whatever reason - fall through and mint a new one. A fresh id costs
            // one address rebuild, which is far better than refusing to connect.
        }

        var bytes = new byte[8];
        RandomNumberGenerator.Fill(bytes);
        var id = BinaryPrimitives.ReadUInt64BigEndian(bytes);

        try
        {
            Directory.CreateDirectory(ServiceConfig.DefaultDirectory);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception)
        {
            // Cannot persist it; the id still works for this run, the client just will not be
            // recognised after a restart.
        }

        return id;
    }
}
