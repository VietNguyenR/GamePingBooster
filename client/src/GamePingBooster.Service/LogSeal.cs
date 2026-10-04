using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GamePingBooster.Service;

/// <summary>
/// Seals the part of a log line that names a domain or an IPv4 address, so the unblock's names and edges cannot be
/// copied out of a player's log by whoever opens it. Only the operator, who holds the private half of
/// <see cref="Modulus"/>, can read them back; everything before the first name stays as it was, so a line still says
/// what it is about:
///
///   Unblock: [enc:1a2b3c4d:&lt;base64 iv | ciphertext | mac&gt;]
///
/// One random 64-byte key per log (32 for AES-256-CBC, 32 for HMAC-SHA256, mac cut to 16 bytes), wrapped with RSA-OAEP
/// (SHA-1) for the operator's key and written as a <see cref="KeyLine"/> at the top of every file and every
/// <see cref="KeyLineEvery"/> sealed lines, so a piece of a log still carries its key. The id is the first 8 hex digits
/// of SHA-256 over the wrapped key. tools/Check-Unblock.ps1 seals its report the same way, and has to: it runs on
/// Windows PowerShell 5.1, whose .NET Framework has neither AesGcm nor OAEP-SHA256 - hence CBC + HMAC and SHA-1.
///
/// This hides the names from a casual reader, not from a determined one: the same names are in the Windows name
/// resolution policy, the route table and the profile on disk. It keeps the log from being the easy copy.
/// </summary>
internal sealed class LogSeal
{
    /// <summary>The operator's public key, RSA-3072. A build with another operator's key reads its own logs.</summary>
    internal const string Modulus = "zD+EArW9LeU6g9/o1JOuZ+M8nvZ969eFztqxzeij6Kw503pb13oB4bjMQgYaPYTolleBP08yo6n1f3gAURJgKQWEFVNvnZiNTx6tANRx5td5T/DkXk7xZhEOoJ59hGaEK4OX4gLMALzJ2/g340JZZq+c7k4uWy4uPrZMpyu4rAysmXBjcb4en4VXqQzRVcQ8CzJiTR2zqdJjyne5j8Bv1ajJmzyTjN9Zah3+Cjnat9O2Oe4KBB18nim0+pxY5eP1FUowTTE9jpLYQp+wiJeS4fVFWO2fkYR3jtWNOacIkoGLJkY9vOugryRLpQOLTA7N2IGq9iCoOoNMiz1o7/VW5bLs7JMNpRa8lStt66aQTnxCj4Hgb9vd3Mbg6o1yNVxYhbCey7ygwcTzTMQu+zZUpq+s3rQvM70WCJSXHAKl8NkG3AbulfDSFutmRXtfZeGEbWckXdVm5so3xgj4oPtsLv/iae1aFzPUz5EYzRmXRDK3gywHo4KCgzOVYRhxD4ut";
    internal const string Exponent = "AQAB";

    internal const int KeyLineEvery = 300;

    // A dotted quad, or a name ending in a label that starts with a letter. File names are not names: see NotAName.
    private static readonly Regex Sensitive = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b|\b(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{1,62}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "json", "jsonl", "log", "txt", "csv", "ps1", "cs", "sys", "config", "xml", "ini", "dat", "tmp",
        "bak", "md", "pdb", "msi", "zip",
    };

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(64);
    private int _sinceKeyLine;

    public LogSeal()
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Convert.FromBase64String(Modulus),
            Exponent = Convert.FromBase64String(Exponent),
        });
        var wrapped = rsa.Encrypt(_key, RSAEncryptionPadding.OaepSHA1);
        Id = Convert.ToHexString(SHA256.HashData(wrapped), 0, 4).ToLowerInvariant();
        KeyLine = $"--- log key k={Id} [key:{Convert.ToBase64String(wrapped)}] ---";
    }

    public string Id { get; }

    /// <summary>The line that lets the operator open this log's sealed parts.</summary>
    public string KeyLine { get; }

    /// <summary>True when the key line is due again - after <see cref="KeyLineEvery"/> sealed lines. Resets the count.</summary>
    public bool KeyLineDue()
    {
        if (_sinceKeyLine < KeyLineEvery) return false;
        _sinceKeyLine = 0;
        return true;
    }

    /// <summary>The message with everything from its first domain name or IPv4 address on sealed; unchanged without one.</summary>
    public string Seal(string message)
    {
        var at = FirstSensitive(message);
        if (at < 0) return message;
        _sinceKeyLine++;
        return message[..at] + $"[enc:{Id}:{Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(message[at..])))}]";
    }

    internal static int FirstSensitive(string message)
    {
        for (var m = Sensitive.Match(message); m.Success; m = m.NextMatch())
        {
            if (char.IsDigit(m.Value[0]) && m.Value.Count(c => c == '.') == 3) return m.Index;
            var last = m.Value[(m.Value.LastIndexOf('.') + 1)..];
            if (!FileExtensions.Contains(last)) return m.Index;
        }
        return -1;
    }

    private byte[] Encrypt(byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = _key[..32];
        aes.GenerateIV();
        var body = aes.EncryptCbc(plain, aes.IV, PaddingMode.PKCS7);
        var sealedBytes = new byte[16 + body.Length + 16];
        aes.IV.CopyTo(sealedBytes, 0);
        body.CopyTo(sealedBytes, 16);
        var mac = HMACSHA256.HashData(_key[32..], sealedBytes.AsSpan(0, 16 + body.Length));
        mac.AsSpan(0, 16).CopyTo(sealedBytes.AsSpan(16 + body.Length));
        return sealedBytes;
    }
}
