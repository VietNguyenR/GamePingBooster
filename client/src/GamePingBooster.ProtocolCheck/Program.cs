using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GamePingBooster.Core.Protocol;

namespace GamePingBooster.ProtocolCheck;

/// <summary>
/// Checks that this client's wire format still matches the relay's, byte for byte.
///
/// The format has two implementations - GpbProtocol.cs here and relay/internal/protocol in Go -
/// and nothing in either build fails when they drift apart. The symptom of drift is not a
/// compile error: it is a tunnel that handshakes and then carries nothing, or one that misreads
/// a field and hands a player's traffic to the wrong session. Either way it shows up on a
/// player's PC rather than on the machine where the change was made.
///
/// Both sides therefore check themselves against the same committed file of golden packets,
/// testdata/protocol-vectors.json, which the Go side generates. Run this after touching either
/// implementation:
///
///     dotnet run --project client/src/GamePingBooster.ProtocolCheck
///
/// It is a plain console program on purpose. This repository carries no test framework, and a
/// protocol check is a list of assertions - not worth a NuGet dependency that every future
/// contributor would have to restore before they could build.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        // Go can regenerate its own P-256 signature but obviously not this one, so the committed
        // .NET signature is produced here and pasted into the vector file by hand. It only has to
        // be redone when the fixed key or the message changes.
        if (args.Length > 0 && args[0] == "--emit-p256-signature")
        {
            return EmitP256Signature(args.Length > 1 ? args[1] : null);
        }

        string path;
        try
        {
            path = args.Length > 0 ? args[0] : FindVectorFile();
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No vector file at {path}");
            return 2;
        }

        Console.WriteLine($"Checking the wire format against {path}");

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var psk = Encoding.UTF8.GetBytes(root.GetProperty("psk").GetString()!);

        var version = root.GetProperty("version").GetInt32();
        Check("protocol version", version == GpbProtocol.Version,
            $"the vectors are for v{version}, this client speaks v{GpbProtocol.Version}");

        CheckHandshakeReq(root.GetProperty("handshakeReq"), psk);
        CheckHandshakeResp(root.GetProperty("handshakeResp"), psk);
        CheckVersionMismatchResp(root.GetProperty("versionMismatchResp"), psk);
        CheckData(root.GetProperty("data"));
        CheckPing(root.GetProperty("ping"));
        CheckPong(root.GetProperty("pong"));
        CheckDisconnect(root.GetProperty("disconnect"));
        CheckCryptoP256(root.GetProperty("cryptoP256"));

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("OK - the C# client and the Go relay agree on every byte.");
            return 0;
        }
        Console.Error.WriteLine($"FAILED - {_failures} check(s) did not match. The client and the relay " +
                                "would not understand each other. Fix GpbProtocol.cs, or regenerate the " +
                                "vectors only if the format change was deliberate and the Go side is done.");
        return 1;
    }

    // ------------------------------------------------------------------ checks

    /// <summary>
    /// HandshakeReq cannot be rebuilt byte for byte here - the nonce is random - so this checks
    /// the two things that actually have to agree: where each field sits, and how the signature
    /// is computed over them.
    /// </summary>
    private static void CheckHandshakeReq(JsonElement v, byte[] psk)
    {
        var pkt = Hex(v.GetProperty("packetHex").GetString()!);
        var clientId = HexToUInt64(v.GetProperty("clientIdHex").GetString()!);
        var unixTime = v.GetProperty("unixTimeSeconds").GetInt64();
        var nonce = v.GetProperty("nonceHex").GetString()!;

        Check("HandshakeReq length", pkt.Length == GpbProtocol.HandshakeReqLen,
            $"got {pkt.Length}, want {GpbProtocol.HandshakeReqLen}");
        Check("HandshakeReq header byte", pkt[0] == (GpbProtocol.Version << 4 | GpbProtocol.TypeHandshakeReq),
            $"got 0x{pkt[0]:x2}");
        Check("HandshakeReq nonce offset", ToHex(pkt.AsSpan(1, 8)) == nonce,
            $"read {ToHex(pkt.AsSpan(1, 8))}, want {nonce}");
        Check("HandshakeReq timestamp offset",
            BinaryPrimitives.ReadUInt64BigEndian(pkt.AsSpan(9, 8)) == (ulong)unixTime,
            $"read {BinaryPrimitives.ReadUInt64BigEndian(pkt.AsSpan(9, 8))}, want {unixTime}");
        Check("HandshakeReq client id offset",
            BinaryPrimitives.ReadUInt64BigEndian(pkt.AsSpan(17, 8)) == clientId,
            "the relay would hand this client the wrong reserved address");

        // The signature is what the relay checks, and it is computed over the first 25 bytes.
        var expected = HMACSHA256.HashData(psk, pkt.AsSpan(0, 25));
        Check("HandshakeReq signature", expected.AsSpan().SequenceEqual(pkt.AsSpan(25)),
            "the relay would reject every handshake this client sends");

        // And what this client builds today must still have that shape.
        var built = GpbProtocol.BuildHandshakeReq(psk, clientId, DateTimeOffset.FromUnixTimeSeconds(unixTime));
        Check("BuildHandshakeReq length", built.Length == GpbProtocol.HandshakeReqLen,
            $"got {built.Length}");
        Check("BuildHandshakeReq header", built[0] == pkt[0], $"got 0x{built[0]:x2}, want 0x{pkt[0]:x2}");
        Check("BuildHandshakeReq timestamp",
            BinaryPrimitives.ReadUInt64BigEndian(built.AsSpan(9, 8)) == (ulong)unixTime, "wrong offset or endianness");
        Check("BuildHandshakeReq client id",
            BinaryPrimitives.ReadUInt64BigEndian(built.AsSpan(17, 8)) == clientId, "wrong offset or endianness");
        Check("BuildHandshakeReq signature",
            HMACSHA256.HashData(psk, built.AsSpan(0, 25)).AsSpan().SequenceEqual(built.AsSpan(25)),
            "the packet this client sends is not signed over the range the relay verifies");
    }

    private static void CheckHandshakeResp(JsonElement v, byte[] psk)
    {
        var pkt = Hex(v.GetProperty("packetHex").GetString()!);

        if (!GpbProtocol.TryParseHandshakeResp(psk, pkt, out var result))
        {
            Fail("HandshakeResp parse", "the client rejects the answer the relay sends - no tunnel would ever come up");
            return;
        }
        Check("HandshakeResp status", result.Status == v.GetProperty("status").GetInt32(),
            $"got {result.Status}");
        Check("HandshakeResp session id",
            result.SessionId == HexToUInt64(v.GetProperty("sessionIdHex").GetString()!),
            $"got 0x{result.SessionId:x16} - every Data packet would name a session the relay does not know");
        Check("HandshakeResp client IP",
            result.ClientIp.ToString() == v.GetProperty("clientIp").GetString(),
            $"got {result.ClientIp} - the virtual adapter would be given the wrong address");
        Check("HandshakeResp relay IP",
            result.RelayIp.ToString() == v.GetProperty("relayIp").GetString(), $"got {result.RelayIp}");
        Check("HandshakeResp MTU", result.Mtu == v.GetProperty("mtu").GetInt32(),
            $"got {result.Mtu} - a wrong MTU means large packets vanish silently");
    }

    /// <summary>
    /// The relay answers a client of another version with a packet carrying the CLIENT's version
    /// in the header, so the client can still parse it and say so. If this check fails, a version
    /// mismatch degrades back into an unexplained timeout.
    /// </summary>
    private static void CheckVersionMismatchResp(JsonElement v, byte[] psk)
    {
        var pkt = Hex(v.GetProperty("packetHex").GetString()!);
        if (!GpbProtocol.TryParseHandshakeResp(psk, pkt, out var result))
        {
            Fail("version-mismatch answer", "the client cannot parse it, so it would report a timeout instead");
            return;
        }
        Check("version-mismatch status", result.Status == GpbProtocol.StatusVersionMismatch,
            $"got {result.Status}");
    }

    private static void CheckData(JsonElement v)
    {
        var expected = Hex(v.GetProperty("packetHex").GetString()!);
        var inner = Hex(v.GetProperty("innerHex").GetString()!);
        var sid = HexToUInt64(v.GetProperty("sessionIdHex").GetString()!);

        var buf = new byte[GpbProtocol.MaxPacketLen];
        var n = GpbProtocol.WriteData(buf, sid, inner);
        Check("WriteData", buf.AsSpan(0, n).SequenceEqual(expected),
            $"got {ToHex(buf.AsSpan(0, n))}, want {ToHex(expected)}");

        if (!GpbProtocol.TryReadData(expected, out var readSid, out var readInner))
        {
            Fail("TryReadData", "the client drops the Data packets the relay sends - the tunnel carries nothing");
            return;
        }
        Check("TryReadData session id", readSid == sid, $"got 0x{readSid:x16}");
        Check("TryReadData payload", readInner.SequenceEqual(inner), "the inner IP packet came back altered");
    }

    private static void CheckPing(JsonElement v)
    {
        var expected = Hex(v.GetProperty("packetHex").GetString()!);
        var sid = HexToUInt64(v.GetProperty("sessionIdHex").GetString()!);
        var stamp = v.GetProperty("stamp").GetUInt64();

        Check("BuildPing", GpbProtocol.BuildPing(sid, stamp).AsSpan().SequenceEqual(expected),
            "the relay would ignore this client's keepalives and time the session out mid-game");
    }

    private static void CheckPong(JsonElement v)
    {
        var pkt = Hex(v.GetProperty("packetHex").GetString()!);
        var sid = HexToUInt64(v.GetProperty("sessionIdHex").GetString()!);
        var stamp = v.GetProperty("stamp").GetUInt64();

        if (!GpbProtocol.TryReadPong(pkt, out var readSid, out var readStamp))
        {
            Fail("TryReadPong", "the client never sees an answer, so the supervisor reconnects every 15 seconds forever");
            return;
        }
        Check("TryReadPong session id", readSid == sid, $"got 0x{readSid:x16}");
        Check("TryReadPong stamp", readStamp == stamp,
            $"got 0x{readStamp:x16} - every latency number in the UI, including relay selection, would be fiction");
    }

    private static void CheckDisconnect(JsonElement v)
    {
        var expected = Hex(v.GetProperty("packetHex").GetString()!);
        var sid = HexToUInt64(v.GetProperty("sessionIdHex").GetString()!);
        Check("BuildDisconnect", GpbProtocol.BuildDisconnect(sid).AsSpan().SequenceEqual(expected),
            "the relay would hold the session open until it times out");
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// P-256 agreement with the Go side, for the v3 handshake.
    ///
    /// The check that matters is verifying the signature GO made. This program verifying its own
    /// output would pass even if the two standard libraries disagreed completely - which is the
    /// whole reason a cross-language vector file exists.
    /// </summary>
    private static void CheckCryptoP256(JsonElement v)
    {
        var d = Hex(v.GetProperty("privateKeyHex").GetString()!);
        var expectedPub = v.GetProperty("publicKeyHex").GetString()!;
        var message = Hex(v.GetProperty("messageHex").GetString()!);
        var goSig = Hex(v.GetProperty("signatureFromGoHex").GetString()!);
        var dotnetSig = v.GetProperty("signatureFromDotnetHex").GetString()!;

        using var priv = GpbCrypto.ImportPrivateKey(d);

        Check("P-256 public key derived from the committed scalar",
            ToHex(GpbCrypto.ExportPublicKey(priv)) == expectedPub,
            $"got {ToHex(GpbCrypto.ExportPublicKey(priv))}, want {expectedPub}");

        using var pub = GpbCrypto.ImportPublicKey(Hex(expectedPub));

        Check("P-256 verify a signature made by Go",
            GpbCrypto.Verify(pub, message, goSig),
            "the two standard libraries disagree - v3 handshakes would be rejected in one direction");

        // Signing is randomised, so this cannot be compared against a fixed value. What it does
        // prove is that this side produces something the same key verifies, at the right length.
        var fresh = GpbCrypto.Sign(priv, message);
        Check("P-256 signature length", fresh.Length == GpbCrypto.SignatureLen,
            $"got {fresh.Length}, want {GpbCrypto.SignatureLen} - r and s must each be padded to 32 bytes");
        Check("P-256 round trip", GpbCrypto.Verify(pub, message, fresh), "signed here, rejected here");

        Check("P-256 rejects a tampered message",
            !GpbCrypto.Verify(pub, Hex("00"), goSig), "verification is not actually checking anything");

        if (dotnetSig.Length == 0)
        {
            Fail("P-256 committed .NET signature",
                "empty - regenerate with: dotnet run --project client/src/GamePingBooster.ProtocolCheck " +
                "-- --emit-p256-signature");
        }
        else
        {
            Check("P-256 verify the committed .NET signature",
                GpbCrypto.Verify(pub, message, Hex(dotnetSig)),
                "the committed signature was made with a different key or message");
        }
    }

    /// <summary>Prints a fresh r||s signature over the committed message, for pasting into the vectors.</summary>
    private static int EmitP256Signature(string? vectorPath)
    {
        string path;
        try
        {
            path = vectorPath ?? FindVectorFile();
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var v = doc.RootElement.GetProperty("cryptoP256");
        using var priv = GpbCrypto.ImportPrivateKey(Hex(v.GetProperty("privateKeyHex").GetString()!));
        var message = Hex(v.GetProperty("messageHex").GetString()!);

        Console.WriteLine(ToHex(GpbCrypto.Sign(priv, message)));
        return 0;
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            Console.WriteLine($"  ok    {name}");
            return;
        }
        Fail(name, detail);
    }

    private static void Fail(string name, string detail)
    {
        _failures++;
        Console.WriteLine($"  FAIL  {name}: {detail}");
    }

    /// <summary>
    /// Walks up from the binary until it finds the repository's testdata directory, so the tool
    /// works from any working directory.
    /// </summary>
    private static string FindVectorFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "testdata", "protocol-vectors.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not find testdata/protocol-vectors.json in any parent directory. Generate it with: " +
            "cd relay && GPB_UPDATE_VECTORS=1 go test ./internal/protocol/ -run TestProtocolVectors");
    }

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    private static string ToHex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    private static ulong HexToUInt64(string s) =>
        ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
