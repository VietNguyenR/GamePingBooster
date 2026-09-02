package protocol

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"net/netip"
	"os"
	"testing"
	"time"
)

// The wire format lives in two implementations that must agree byte for byte: this package and
// client/src/GamePingBooster.Core/Protocol/GpbProtocol.cs. Nothing in the build catches them
// drifting apart, and the symptom of drift is not a compile error - it is a tunnel that
// handshakes and then silently carries nothing, or worse, one that misreads a field and routes
// a player's traffic into the wrong session.
//
// So both sides check themselves against the same committed file of golden packets. Go writes
// it; Go and C# both assert against it. A change to either implementation that alters a single
// byte fails here, on the machine where it was made, instead of on a player's PC.
//
// Regenerate deliberately, never casually - a regenerated file makes any drift look correct:
//
//	GPB_UPDATE_VECTORS=1 go test ./internal/protocol/ -run TestProtocolVectors
const vectorPath = "../../../testdata/protocol-vectors.json"

// Fixed inputs. These values are arbitrary but must never change: they are what the committed
// packets were built from.
const (
	vectorPSK       = "a-test-psk-of-at-least-16-bytes"
	vectorUnixTime  = 1767225600 // 2026-01-01T00:00:00Z
	vectorStamp     = uint64(0x0123456789abcdef)
	vectorMTU       = 1400
	vectorClientIP  = "10.77.0.5"
	vectorRelayIP   = "10.77.0.1"
	vectorInnerHex  = "450000200000000040110000" + "0a4d0005" + "01010101" + "1f901f9000080000"
	vectorClientHex = "0102030405060708"
	vectorSessHex   = "1122334455667788"

	// A fixed P-256 key for the cross-language checks. Generated once and frozen: the committed
	// signatures were made with it, so changing it invalidates every one of them.
	vectorP256PrivHex = "58ee53ec7ce1815650f98f5aa2275a2d8aa7839c5ceb40f2253d39b0037510a3"
	vectorP256PubHex  = "0430906cdb359b22917fd8ea326d18d606f61f001430addf2fd62afa02d3d1e2" +
		"83c3c9dba8f9d5eb11ae0fffe2cc9ad0e90a90f7a220b7288513342abd93d90604"

	// 64 bytes of 0x00..0x3f. Arbitrary, but it exercises a full block and is trivially
	// reproducible by hand on the other side.
	vectorP256MsgHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f" +
		"202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f"
)

type vectorFile struct {
	Note    string `json:"note"`
	PSK     string `json:"psk"`
	Version int    `json:"version"`

	HandshakeReq struct {
		ClientIDHex     string `json:"clientIdHex"`
		UnixTimeSeconds int64  `json:"unixTimeSeconds"`
		NonceHex        string `json:"nonceHex"`
		PacketHex       string `json:"packetHex"`
	} `json:"handshakeReq"`

	HandshakeResp struct {
		Status       int    `json:"status"`
		NonceHex     string `json:"nonceHex"`
		SessionIDHex string `json:"sessionIdHex"`
		ClientIP     string `json:"clientIp"`
		RelayIP      string `json:"relayIp"`
		MTU          int    `json:"mtu"`
		PacketHex    string `json:"packetHex"`
	} `json:"handshakeResp"`

	VersionMismatchResp struct {
		ClientVersion int    `json:"clientVersion"`
		PacketHex     string `json:"packetHex"`
	} `json:"versionMismatchResp"`

	Data struct {
		SessionIDHex string `json:"sessionIdHex"`
		InnerHex     string `json:"innerHex"`
		PacketHex    string `json:"packetHex"`
	} `json:"data"`

	Ping struct {
		SessionIDHex string `json:"sessionIdHex"`
		Stamp        uint64 `json:"stamp"`
		PacketHex    string `json:"packetHex"`
	} `json:"ping"`

	Pong struct {
		SessionIDHex string `json:"sessionIdHex"`
		Stamp        uint64 `json:"stamp"`
		PacketHex    string `json:"packetHex"`
	} `json:"pong"`

	Disconnect struct {
		SessionIDHex string `json:"sessionIdHex"`
		PacketHex    string `json:"packetHex"`
	} `json:"disconnect"`

	// P-256 agreement between the two standard libraries, for the v3 handshake. Each side
	// verifies a signature the OTHER one made: that is the only thing that proves they agree,
	// because either side verifying its own output proves nothing.
	//
	// ECDSA signing is randomised, so these are not reproducible - they are frozen samples.
	CryptoP256 struct {
		Note                   string `json:"note"`
		PrivateKeyHex          string `json:"privateKeyHex"`
		PublicKeyHex           string `json:"publicKeyHex"`
		MessageHex             string `json:"messageHex"`
		SignatureFromGoHex     string `json:"signatureFromGoHex"`
		SignatureFromDotnetHex string `json:"signatureFromDotnetHex"`
	} `json:"cryptoP256"`
}

func mustHex(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatalf("bad hex %q: %v", s, err)
	}
	return b
}

func vectorIDs(t *testing.T) (ClientID, SessionID) {
	t.Helper()
	var cid ClientID
	var sid SessionID
	copy(cid[:], mustHex(t, vectorClientHex))
	copy(sid[:], mustHex(t, vectorSessHex))
	return cid, sid
}

func generateVectors(t *testing.T) {
	t.Helper()
	psk := []byte(vectorPSK)
	cid, sid := vectorIDs(t)
	now := time.Unix(vectorUnixTime, 0)

	req, nonce, err := BuildHandshakeReq(psk, cid, now)
	if err != nil {
		t.Fatalf("build handshake req: %v", err)
	}

	var v vectorFile
	v.Note = "Golden packets shared by the Go relay and the C# client. See relay/internal/protocol/vectors_test.go."
	v.PSK = vectorPSK
	v.Version = Version

	v.HandshakeReq.ClientIDHex = vectorClientHex
	v.HandshakeReq.UnixTimeSeconds = vectorUnixTime
	v.HandshakeReq.NonceHex = hex.EncodeToString(nonce[:])
	v.HandshakeReq.PacketHex = hex.EncodeToString(req)

	v.HandshakeResp.Status = StatusOK
	v.HandshakeResp.SessionIDHex = vectorSessHex
	v.HandshakeResp.ClientIP = vectorClientIP
	v.HandshakeResp.RelayIP = vectorRelayIP
	v.HandshakeResp.MTU = vectorMTU
	v.HandshakeResp.NonceHex = hex.EncodeToString(nonce[:])
	v.HandshakeResp.PacketHex = hex.EncodeToString(BuildHandshakeResp(psk, StatusOK, sid,
		netip.MustParseAddr(vectorClientIP), netip.MustParseAddr(vectorRelayIP), vectorMTU, nonce))

	v.VersionMismatchResp.ClientVersion = Version + 1
	v.VersionMismatchResp.PacketHex = hex.EncodeToString(BuildVersionMismatchResp(psk, Version+1))

	buf := make([]byte, MaxPacketLen)
	v.Data.SessionIDHex = vectorSessHex
	v.Data.InnerHex = vectorInnerHex
	v.Data.PacketHex = hex.EncodeToString(EncodeData(buf, sid, mustHex(t, vectorInnerHex)))

	v.Ping.SessionIDHex = vectorSessHex
	v.Ping.Stamp = vectorStamp
	v.Ping.PacketHex = hex.EncodeToString(BuildPing(sid, vectorStamp))

	v.Pong.SessionIDHex = vectorSessHex
	v.Pong.Stamp = vectorStamp
	v.Pong.PacketHex = hex.EncodeToString(BuildPong(sid, vectorStamp))

	v.Disconnect.SessionIDHex = vectorSessHex
	v.Disconnect.PacketHex = hex.EncodeToString(BuildDisconnect(sid))

	// ------------------------------------------------------------ P-256
	//
	// Go can regenerate its own signature but obviously not .NET's, so the existing one is
	// carried across from the committed file. Losing it would leave the Go side verifying only
	// its own output, which proves nothing and would still pass - the exact shape of a test
	// that cannot fail.
	v.CryptoP256.Note = "Each side verifies the signature the other made. Regenerate .NET's with: " +
		"dotnet run --project client/src/GamePingBooster.ProtocolCheck -- --emit-p256-signature"
	v.CryptoP256.PrivateKeyHex = vectorP256PrivHex
	v.CryptoP256.PublicKeyHex = vectorP256PubHex
	v.CryptoP256.MessageHex = vectorP256MsgHex

	p256Key, err := ParsePrivateKey(mustHex(t, vectorP256PrivHex))
	if err != nil {
		t.Fatalf("parse the fixed P-256 key: %v", err)
	}
	goSig, err := Sign(p256Key, mustHex(t, vectorP256MsgHex))
	if err != nil {
		t.Fatalf("sign with the fixed P-256 key: %v", err)
	}
	v.CryptoP256.SignatureFromGoHex = hex.EncodeToString(goSig)

	if prev, err := os.ReadFile(vectorPath); err == nil {
		var old vectorFile
		if json.Unmarshal(prev, &old) == nil {
			v.CryptoP256.SignatureFromDotnetHex = old.CryptoP256.SignatureFromDotnetHex
		}
	}
	if v.CryptoP256.SignatureFromDotnetHex == "" {
		t.Log("no .NET signature carried over - produce one and paste it in, or the " +
			"cross-language half of this check is not running")
	}

	out, err := json.MarshalIndent(&v, "", "  ")
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	if err := os.WriteFile(vectorPath, append(out, '\n'), 0o644); err != nil {
		t.Fatalf("write %s: %v", vectorPath, err)
	}
	t.Logf("wrote %s", vectorPath)
}

func loadVectors(t *testing.T) *vectorFile {
	t.Helper()
	raw, err := os.ReadFile(vectorPath)
	if err != nil {
		t.Fatalf("read %s: %v (generate it with GPB_UPDATE_VECTORS=1)", vectorPath, err)
	}
	var v vectorFile
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("parse %s: %v", vectorPath, err)
	}
	return &v
}

func TestProtocolVectors(t *testing.T) {
	if os.Getenv("GPB_UPDATE_VECTORS") == "1" {
		generateVectors(t)
	}

	v := loadVectors(t)
	psk := []byte(v.PSK)
	cid, sid := vectorIDs(t)

	if v.Version != Version {
		t.Fatalf("the vectors are for protocol v%d, this build speaks v%d", v.Version, Version)
	}

	// ------------------------------------------------------------ HandshakeReq
	// Not rebuildable byte for byte (the nonce is random), so verify it the way the relay does.
	reqPkt := mustHex(t, v.HandshakeReq.PacketHex)
	if len(reqPkt) != HandshakeReqPSKLen {
		t.Errorf("HandshakeReq is %d bytes, want %d", len(reqPkt), HandshakeReqPSKLen)
	}
	if reqPkt[1] != AuthModePSK {
		t.Errorf("auth mode byte is %d, want AuthModePSK", reqPkt[1])
	}
	gotID, _, err := VerifyHandshakeReq(psk, reqPkt, time.Unix(v.HandshakeReq.UnixTimeSeconds, 0))
	if err != nil {
		t.Errorf("the golden HandshakeReq no longer verifies: %v", err)
	} else if gotID != cid {
		t.Errorf("client id read as %x, want %x", gotID, cid)
	}
	if got := hex.EncodeToString(reqPkt[2:10]); got != v.HandshakeReq.NonceHex {
		t.Errorf("nonce is at the wrong offset: read %s, want %s", got, v.HandshakeReq.NonceHex)
	}

	// ----------------------------------------------------------- HandshakeResp
	respPkt := mustHex(t, v.HandshakeResp.PacketHex)
	var respNonce [8]byte
	copy(respNonce[:], mustHex(t, v.HandshakeResp.NonceHex))
	rebuilt := BuildHandshakeResp(psk, byte(v.HandshakeResp.Status), sid,
		netip.MustParseAddr(v.HandshakeResp.ClientIP),
		netip.MustParseAddr(v.HandshakeResp.RelayIP),
		uint16(v.HandshakeResp.MTU), respNonce)
	if !bytes.Equal(rebuilt, respPkt) {
		t.Errorf("HandshakeResp changed:\n got %x\nwant %x", rebuilt, respPkt)
	}
	parsed, err := ParseHandshakeResp(psk, respPkt, respNonce)
	if err != nil {
		t.Fatalf("the golden HandshakeResp no longer parses: %v", err)
	}
	if parsed.Session != sid {
		t.Errorf("session id read as %x, want %x", parsed.Session, sid)
	}
	if parsed.ClientIP.String() != v.HandshakeResp.ClientIP {
		t.Errorf("client IP read as %s, want %s", parsed.ClientIP, v.HandshakeResp.ClientIP)
	}
	if parsed.RelayIP.String() != v.HandshakeResp.RelayIP {
		t.Errorf("relay IP read as %s, want %s", parsed.RelayIP, v.HandshakeResp.RelayIP)
	}
	if int(parsed.MTU) != v.HandshakeResp.MTU {
		t.Errorf("MTU read as %d, want %d", parsed.MTU, v.HandshakeResp.MTU)
	}

	// ---------------------------------------------------- version mismatch resp
	mismatch := BuildVersionMismatchResp(psk, byte(v.VersionMismatchResp.ClientVersion))
	if !bytes.Equal(mismatch, mustHex(t, v.VersionMismatchResp.PacketHex)) {
		t.Errorf("the version-mismatch answer changed:\n got %x\nwant %s",
			mismatch, v.VersionMismatchResp.PacketHex)
	}

	// -------------------------------------------------------------------- Data
	inner := mustHex(t, v.Data.InnerHex)
	buf := make([]byte, MaxPacketLen)
	encoded := EncodeData(buf, sid, inner)
	if !bytes.Equal(encoded, mustHex(t, v.Data.PacketHex)) {
		t.Errorf("Data encoding changed:\n got %x\nwant %s", encoded, v.Data.PacketHex)
	}
	decSid, decInner, err := DecodeData(mustHex(t, v.Data.PacketHex))
	if err != nil {
		t.Fatalf("the golden Data packet no longer decodes: %v", err)
	}
	if decSid != sid {
		t.Errorf("session id read as %x, want %x", decSid, sid)
	}
	if !bytes.Equal(decInner, inner) {
		t.Errorf("inner packet read as %x, want %x", decInner, inner)
	}

	// ------------------------------------------------------------- Ping / Pong
	if got := BuildPing(sid, v.Ping.Stamp); !bytes.Equal(got, mustHex(t, v.Ping.PacketHex)) {
		t.Errorf("Ping changed:\n got %x\nwant %s", got, v.Ping.PacketHex)
	}
	if got := BuildPong(sid, v.Pong.Stamp); !bytes.Equal(got, mustHex(t, v.Pong.PacketHex)) {
		t.Errorf("Pong changed:\n got %x\nwant %s", got, v.Pong.PacketHex)
	}
	pingSid, pingStamp, err := DecodePing(mustHex(t, v.Ping.PacketHex))
	if err != nil {
		t.Fatalf("the golden Ping no longer decodes: %v", err)
	}
	if pingSid != sid || pingStamp != v.Ping.Stamp {
		t.Errorf("Ping decoded as %x/%#x, want %x/%#x", pingSid, pingStamp, sid, v.Ping.Stamp)
	}

	// -------------------------------------------------------------- Disconnect
	if got := BuildDisconnect(sid); !bytes.Equal(got, mustHex(t, v.Disconnect.PacketHex)) {
		t.Errorf("Disconnect changed:\n got %x\nwant %s", got, v.Disconnect.PacketHex)
	}

	// ------------------------------------------------------------------ P-256
	//
	// The half that matters is verifying .NET's signature. Go verifying its own output here
	// would pass even if the two libraries disagreed completely.
	p256Msg := mustHex(t, v.CryptoP256.MessageHex)

	p256Priv, err := ParsePrivateKey(mustHex(t, v.CryptoP256.PrivateKeyHex))
	if err != nil {
		t.Fatalf("the committed P-256 private key does not parse: %v", err)
	}
	if got := hex.EncodeToString(MarshalPublicKey(&p256Priv.PublicKey)); got != v.CryptoP256.PublicKeyHex {
		t.Errorf("public key derived from the committed scalar\n got %s\nwant %s",
			got, v.CryptoP256.PublicKeyHex)
	}

	p256Pub, err := ParsePublicKey(mustHex(t, v.CryptoP256.PublicKeyHex))
	if err != nil {
		t.Fatalf("the committed P-256 public key does not parse: %v", err)
	}

	if !Verify(p256Pub, p256Msg, mustHex(t, v.CryptoP256.SignatureFromGoHex)) {
		t.Error("Go cannot verify its own committed signature - the format changed")
	}

	if v.CryptoP256.SignatureFromDotnetHex == "" {
		t.Error("no .NET signature in the vectors: the cross-language check is not running, " +
			"which is worse than it failing, because it looks like it passed")
	} else if !Verify(p256Pub, p256Msg, mustHex(t, v.CryptoP256.SignatureFromDotnetHex)) {
		t.Error("Go REJECTED a signature made by .NET - the two libraries disagree")
	}
}
