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

        if (args.Length > 0 && args[0] == "--emit-handshake-req-token")
        {
            return EmitHandshakeReqToken(args.Length > 1 ? args[1] : null);
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
        CheckHandshakeReqToken(root.GetProperty("handshakeReqToken"));
        CheckProfileEnvelope(root);

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

        // Every offset below moved by one in v3, when the auth-mode byte was inserted after the
        // header. That shift is the easiest thing to get wrong in the whole format, which is why
        // each one is asserted separately rather than by comparing whole packets.
        Check("HandshakeReq length", pkt.Length == GpbProtocol.HandshakeReqPskLen,
            $"got {pkt.Length}, want {GpbProtocol.HandshakeReqPskLen}");
        Check("HandshakeReq header byte", pkt[0] == (GpbProtocol.Version << 4 | GpbProtocol.TypeHandshakeReq),
            $"got 0x{pkt[0]:x2}");
        Check("HandshakeReq auth mode", pkt[1] == GpbProtocol.AuthModePsk,
            $"got {pkt[1]}, want AuthModePsk - a licensed relay would drop this in silence");
        Check("HandshakeReq nonce offset", ToHex(pkt.AsSpan(2, 8)) == nonce,
            $"read {ToHex(pkt.AsSpan(2, 8))}, want {nonce}");
        Check("HandshakeReq timestamp offset",
            BinaryPrimitives.ReadUInt64BigEndian(pkt.AsSpan(10, 8)) == (ulong)unixTime,
            $"read {BinaryPrimitives.ReadUInt64BigEndian(pkt.AsSpan(10, 8))}, want {unixTime}");
        Check("HandshakeReq client id offset",
            BinaryPrimitives.ReadUInt64BigEndian(pkt.AsSpan(18, 8)) == clientId,
            "the relay would hand this client the wrong reserved address");

        // The signature is what the relay checks, and it is computed over the first 26 bytes.
        var expected = HMACSHA256.HashData(psk, pkt.AsSpan(0, 26));
        Check("HandshakeReq signature", expected.AsSpan().SequenceEqual(pkt.AsSpan(26)),
            "the relay would reject every handshake this client sends");

        // And what this client builds today must still have that shape.
        var built = GpbProtocol.BuildHandshakeReq(psk, clientId,
            DateTimeOffset.FromUnixTimeSeconds(unixTime), out var builtNonce);
        Check("BuildHandshakeReq length", built.Length == GpbProtocol.HandshakeReqPskLen,
            $"got {built.Length}");
        Check("BuildHandshakeReq header", built[0] == pkt[0], $"got 0x{built[0]:x2}, want 0x{pkt[0]:x2}");
        Check("BuildHandshakeReq auth mode", built[1] == GpbProtocol.AuthModePsk, $"got {built[1]}");
        Check("BuildHandshakeReq timestamp",
            BinaryPrimitives.ReadUInt64BigEndian(built.AsSpan(10, 8)) == (ulong)unixTime, "wrong offset or endianness");
        Check("BuildHandshakeReq client id",
            BinaryPrimitives.ReadUInt64BigEndian(built.AsSpan(18, 8)) == clientId, "wrong offset or endianness");
        Check("BuildHandshakeReq signature",
            HMACSHA256.HashData(psk, built.AsSpan(0, 26)).AsSpan().SequenceEqual(built.AsSpan(26)),
            "the packet this client sends is not signed over the range the relay verifies");

        // The nonce has to come back out, or the caller cannot check the echo in the answer and
        // the replay protection is decorative.
        Check("BuildHandshakeReq returns the nonce it used",
            builtNonce.Length == GpbProtocol.NonceLen
                && builtNonce.AsSpan().SequenceEqual(built.AsSpan(2, GpbProtocol.NonceLen)),
            "the caller cannot verify the echo, so a replayed answer would be accepted");
    }

    private static void CheckHandshakeResp(JsonElement v, byte[] psk)
    {
        var pkt = Hex(v.GetProperty("packetHex").GetString()!);
        var nonce = Hex(v.GetProperty("nonceHex").GetString()!);

        Check("HandshakeResp length", pkt.Length == GpbProtocol.HandshakeRespPskLen,
            $"got {pkt.Length}, want {GpbProtocol.HandshakeRespPskLen}");
        Check("HandshakeResp echoes the nonce",
            ToHex(pkt.AsSpan(20, 8)) == v.GetProperty("nonceHex").GetString(),
            $"read {ToHex(pkt.AsSpan(20, 8))} at offset 20");

        if (!GpbProtocol.TryParseHandshakeResp(psk, pkt, nonce, out var result))
        {
            Fail("HandshakeResp parse", "the client rejects the answer the relay sends - no tunnel would ever come up");
            return;
        }

        // A reply to somebody else's handshake must be refused. Without this the client adopts a
        // session the relay has already forgotten and the tunnel comes up carrying nothing.
        var wrongNonce = new byte[GpbProtocol.NonceLen];
        nonce.CopyTo(wrongNonce, 0);
        wrongNonce[0] ^= 0xff;
        Check("HandshakeResp with the wrong nonce is refused",
            !GpbProtocol.TryParseHandshakeResp(psk, pkt, wrongNonce, out _),
            "a captured answer replayed mid-handshake would be accepted");
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

        // It is the V2 layout, 52 bytes, not v3's 60. A relay one version behind can only send
        // what it knows, so the client has to be able to read that - otherwise "please update"
        // degrades back into the four-attempt timeout this message exists to avoid. The nonce is
        // irrelevant here: the old layout has no echo to check.
        Check("version-mismatch answer is the v2 layout",
            pkt.Length == GpbProtocol.HandshakeRespV2Len,
            $"got {pkt.Length}, want {GpbProtocol.HandshakeRespV2Len}");

        if (!GpbProtocol.TryParseHandshakeResp(psk, pkt, ReadOnlySpan<byte>.Empty, out var result))
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

    /// <summary>
    /// The v3 token handshake, in both directions.
    ///
    /// The half that matters is verifying the packet GO built. This side verifying only its own
    /// output would pass even if every offset in the format had moved, because it would have
    /// moved in the reader too - which is exactly how two implementations drift apart without
    /// anything going red.
    ///
    /// There is no VerifyHandshakeReqToken to lean on here: the client never verifies a request,
    /// only the relay does. So both signatures are checked with the primitives directly, which
    /// has the side effect of stating in code where each signed span begins and ends.
    /// </summary>
    private static void CheckHandshakeReqToken(JsonElement v)
    {
        var tokenHex = v.GetProperty("tokenHex").GetString()!;
        var devicePubHex = v.GetProperty("devicePublicKeyHex").GetString()!;
        var token = Hex(tokenHex);
        var clientId = HexToUInt64(v.GetProperty("clientIdHex").GetString()!);
        var unixTime = v.GetProperty("unixTimeSeconds").GetInt64();

        using var devicePriv = GpbCrypto.ImportPrivateKey(Hex(v.GetProperty("devicePrivateKeyHex").GetString()!));
        using var devicePub = GpbCrypto.ImportPublicKey(Hex(devicePubHex));
        using var licencePub = GpbCrypto.ImportPublicKey(Hex(v.GetProperty("licencePublicKeyHex").GetString()!));

        Check("token handshake: device public key matches the committed scalar",
            ToHex(GpbCrypto.ExportPublicKey(devicePriv)) == devicePubHex,
            "the committed device keypair is inconsistent");

        Check("token handshake: token length", token.Length == GpbProtocol.TokenLen,
            $"got {token.Length}, want {GpbProtocol.TokenLen}");

        // The token's own signature, by the LICENCE key, over everything before it. This is the
        // first check a relay makes, and .NET has never done it before - the client only ever
        // carried a token around as opaque bytes.
        Check("token handshake: the token verifies against the licence key",
            GpbCrypto.Verify(licencePub, token.AsSpan(0, TokenSigOffset), token.AsSpan(TokenSigOffset)),
            "the licence signature over the token does not verify, so the two sides disagree " +
            "about either the token layout or the signature encoding");

        // The device public key sits inside the token. That is what stops a stolen token being
        // useful on its own, so its position is worth asserting rather than assuming.
        Check("token handshake: the token names the committed device key",
            ToHex(token.AsSpan(TokenDeviceKeyOffset, GpbCrypto.PublicKeyLen)) == devicePubHex,
            "the device key is at the wrong offset inside the token");

        void VerifyPacket(string label, string packetHex)
        {
            var pkt = Hex(packetHex);

            Check($"{label}: length", pkt.Length == GpbProtocol.HandshakeReqTokenLen,
                $"got {pkt.Length}, want {GpbProtocol.HandshakeReqTokenLen}");
            if (pkt.Length != GpbProtocol.HandshakeReqTokenLen) return;

            Check($"{label}: header byte",
                pkt[0] == (GpbProtocol.Version << 4 | GpbProtocol.TypeHandshakeReq),
                $"got {pkt[0]:x2}");
            Check($"{label}: auth mode byte", pkt[1] == GpbProtocol.AuthModeToken,
                $"got {pkt[1]}, want {GpbProtocol.AuthModeToken}");
            Check($"{label}: timestamp", ReadUInt64BE(pkt, 10) == (ulong)unixTime,
                $"got {ReadUInt64BE(pkt, 10)}, want {unixTime}");
            Check($"{label}: client id", ReadUInt64BE(pkt, 18) == clientId,
                $"got {ReadUInt64BE(pkt, 18):x16}, want {clientId:x16}");
            Check($"{label}: carries the committed token",
                ToHex(pkt.AsSpan(ReqTokenOffset, GpbProtocol.TokenLen)) == tokenHex,
                "the token is at the wrong offset, or is not the one this file names");

            // The request signature, by the DEVICE key, over everything before it. If this
            // passes for a packet built here and fails for Go's, the two sides disagree about
            // which bytes are signed - the most likely way this format breaks.
            Check($"{label}: device signature verifies",
                GpbCrypto.Verify(devicePub, pkt.AsSpan(0, ReqTokenSigOffset), pkt.AsSpan(ReqTokenSigOffset)),
                "the signature does not cover the span this side thinks it covers");

            // An accept-only check cannot fail. Flip a bit and require a refusal.
            var tampered = (byte[])pkt.Clone();
            tampered[18] ^= 0x01;
            Check($"{label}: one flipped bit is rejected",
                !GpbCrypto.Verify(devicePub, tampered.AsSpan(0, ReqTokenSigOffset), tampered.AsSpan(ReqTokenSigOffset)),
                "a tampered packet still verified, so the check above proves nothing");
        }

        VerifyPacket("token handshake from Go", v.GetProperty("packetFromGoHex").GetString()!);

        // And one built right now, rather than read from the file: it proves the builder in
        // GpbProtocol still produces something that passes every offset check above.
        var mine = GpbProtocol.BuildHandshakeReqToken(devicePriv, token, clientId,
            DateTimeOffset.FromUnixTimeSeconds(unixTime), out _);
        VerifyPacket("token handshake built here", ToHex(mine));
    }

    // Offsets inside a token and inside a token-mode HandshakeReq. See docs/PROTOCOL-v3.md.
    private const int TokenDeviceKeyOffset = 9;
    private const int TokenSigOffset = 86;
    private const int ReqTokenOffset = 26;
    private const int ReqTokenSigOffset = ReqTokenOffset + GpbProtocol.TokenLen;

    private static ulong ReadUInt64BE(ReadOnlySpan<byte> b, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b.Slice(offset, 8));

    /// <summary>
    /// Emits a token-mode HandshakeReq for the Go side to verify.
    ///
    /// Built at the frozen timestamp the vectors name, not at the current time: the relay checks
    /// clock skew, so a packet stamped "now" would stop verifying within a minute of being
    /// committed, and the failure would look like a format bug rather than a stale sample.
    /// </summary>
    private static int EmitHandshakeReqToken(string? vectorPath)
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
        var v = doc.RootElement.GetProperty("handshakeReqToken");

        using var devicePriv = GpbCrypto.ImportPrivateKey(Hex(v.GetProperty("devicePrivateKeyHex").GetString()!));
        var token = Hex(v.GetProperty("tokenHex").GetString()!);
        var clientId = HexToUInt64(v.GetProperty("clientIdHex").GetString()!);
        var unixTime = v.GetProperty("unixTimeSeconds").GetInt64();

        var pkt = GpbProtocol.BuildHandshakeReqToken(devicePriv, token, clientId,
            DateTimeOffset.FromUnixTimeSeconds(unixTime), out _);

        Console.WriteLine(ToHex(pkt));
        return 0;
    }

    /// <summary>
    /// The sealed profile.
    ///
    /// The half that matters is opening an envelope the LICENCE SERVER sealed, in JavaScript -
    /// this side sealing and opening its own would pass even if the two disagreed completely.
    /// That sample lives in the vectors as profileEnvelope.envelopeFromNodeHex; emit a fresh one
    /// with `npm run envelope:emit` in the web-service repository.
    /// </summary>
    private static void CheckProfileEnvelope(JsonElement root)
    {
        // A round trip here first, so a failure below can be read as "the two languages
        // disagree" rather than "this side is broken".
        var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var scalar = deviceKey.ExportParameters(true).D!;
        var devicePublic = GpbCrypto.ExportPublicKey(deviceKey);
        var message = "the quick brown fox"u8.ToArray();

        var sealedHere = ProfileEnvelope.Seal(message, devicePublic);
        Check("profile envelope: round trip",
            ProfileEnvelope.Open(sealedHere, scalar).AsSpan().SequenceEqual(message),
            "sealing and opening on this side disagree, which is a bug here and not a drift");

        // An accept-only check cannot fail.
        var tampered = (byte[])sealedHere.Clone();
        tampered[70] ^= 0x01;
        var refused = false;
        try { ProfileEnvelope.Open(tampered, scalar); }
        catch (CryptographicException) { refused = true; }
        Check("profile envelope: one flipped bit is refused", refused,
            "a tampered envelope opened, so the tag is not being checked");

        // Sealed to somebody else's device.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherScalar = other.ExportParameters(true).D!;
        var wrongDevice = false;
        try { ProfileEnvelope.Open(sealedHere, otherScalar); }
        catch (CryptographicException) { wrongDevice = true; }
        Check("profile envelope: another device cannot open it", wrongDevice,
            "an envelope sealed to one device opened with another key");

        if (!root.TryGetProperty("profileEnvelope", out var v))
        {
            Fail("profile envelope: sealed by Node",
                "no profileEnvelope in the vectors - the cross-language check is not running, " +
                "which is worse than it failing because it looks like it passed");
            return;
        }

        var vectorScalar = Hex(v.GetProperty("devicePrivateKeyHex").GetString()!);
        var expected = v.GetProperty("plaintext").GetString()!;
        var fromNode = Hex(v.GetProperty("envelopeFromNodeHex").GetString()!);

        try
        {
            var opened = ProfileEnvelope.Open(fromNode, vectorScalar);
            Check("profile envelope: sealed by Node, opened here",
                Encoding.UTF8.GetString(opened) == expected,
                $"opened to \"{Encoding.UTF8.GetString(opened)}\", expected \"{expected}\"");
        }
        catch (CryptographicException ex)
        {
            Fail("profile envelope: sealed by Node, opened here",
                $"{ex.Message} - the licence server and this client disagree about the format");
        }
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
