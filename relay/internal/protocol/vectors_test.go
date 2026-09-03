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

	// Two more frozen P-256 keys, for the token handshake. They stand in for the licence
	// server's key and one machine's device key. Frozen for the same reason as the one above:
	// the committed token was signed with the first and the committed packets with the second.
	vectorLicencePrivHex = "3d1f5e2c9b47a8360d5e7f1a2c4b6d8e0f1a3b5c7d9e0f2a4b6c8d0e1f3a5b7c"
	vectorDevicePrivHex  = "6a2c4e8f0b1d3f5a7c9e0b2d4f6a8c0e1f3b5d7f9a1c3e5f7b9d1f3a5c7e9b0d"

	// The token's own fields. The expiry is a day after vectorUnixTime, and every check that
	// touches it passes that same frozen instant as `now` - so this file does not quietly stop
	// working a day after it was written, which is what a real timestamp would do.
	vectorTokenUser    = uint64(4242)
	vectorTokenTier    = byte(1)
	vectorTokenMaxSess = byte(0)
	vectorTokenExpiry  = vectorUnixTime + 86400
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

	// The v3 token handshake, crossed the same way cryptoP256 is: each side verifies the packet
	// the OTHER one built.
	//
	// This is the only thing that proves the 240-byte HandshakeReq agrees between the two
	// implementations. Everything else about token mode was tested within one language: Go's
	// relay tests build and verify Go packets, and the C# client's device key was only ever
	// checked as a 65-byte public key. A shifted field or a signature computed over the wrong
	// span would pass all of that and fail here.
	//
	// ECDSA signing is randomised, so neither packet is reproducible - both are frozen samples,
	// and so is the token, which is embedded inside both of them.
	HandshakeReqToken struct {
		Note                 string `json:"note"`
		LicencePrivateKeyHex string `json:"licencePrivateKeyHex"`
		LicencePublicKeyHex  string `json:"licencePublicKeyHex"`
		DevicePrivateKeyHex  string `json:"devicePrivateKeyHex"`
		DevicePublicKeyHex   string `json:"devicePublicKeyHex"`
		UserID               uint64 `json:"userId"`
		Tier                 int    `json:"tier"`
		MaxSessions          int    `json:"maxSessions"`
		ExpiryUnixSeconds    int64  `json:"expiryUnixSeconds"`
		ClientIDHex          string `json:"clientIdHex"`
		UnixTimeSeconds      int64  `json:"unixTimeSeconds"`
		TokenHex             string `json:"tokenHex"`
		PacketFromGoHex      string `json:"packetFromGoHex"`
		PacketFromDotnetHex  string `json:"packetFromDotnetHex"`
	} `json:"handshakeReqToken"`
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

	// ------------------------------------------------- v3 token handshake
	//
	// Read the previous file FIRST. Three values have to survive a regeneration: .NET's P-256
	// signature, .NET's handshake packet, and the token itself. The token matters most and is
	// the least obvious - it is embedded inside BOTH committed packets, so minting a fresh one
	// here would leave .NET's packet carrying a token this file no longer names, and the
	// mismatch would look like a protocol bug rather than a regeneration artefact.
	var old vectorFile
	if prev, err := os.ReadFile(vectorPath); err == nil {
		_ = json.Unmarshal(prev, &old)
	}
	v.CryptoP256.SignatureFromDotnetHex = old.CryptoP256.SignatureFromDotnetHex
	if v.CryptoP256.SignatureFromDotnetHex == "" {
		t.Log("no .NET signature carried over - produce one and paste it in, or the " +
			"cross-language half of this check is not running")
	}

	licencePriv, err := ParsePrivateKey(mustHex(t, vectorLicencePrivHex))
	if err != nil {
		t.Fatalf("parse the fixed licence key: %v", err)
	}
	devicePriv, err := ParsePrivateKey(mustHex(t, vectorDevicePrivHex))
	if err != nil {
		t.Fatalf("parse the fixed device key: %v", err)
	}

	h := &v.HandshakeReqToken
	h.Note = "Each side verifies the packet the other built. Regenerate .NET's with: " +
		"dotnet run --project client/src/GamePingBooster.ProtocolCheck -- --emit-handshake-req-token"
	h.LicencePrivateKeyHex = vectorLicencePrivHex
	h.LicencePublicKeyHex = hex.EncodeToString(MarshalPublicKey(&licencePriv.PublicKey))
	h.DevicePrivateKeyHex = vectorDevicePrivHex
	h.DevicePublicKeyHex = hex.EncodeToString(MarshalPublicKey(&devicePriv.PublicKey))
	h.UserID = vectorTokenUser
	h.Tier = int(vectorTokenTier)
	h.MaxSessions = int(vectorTokenMaxSess)
	h.ExpiryUnixSeconds = vectorTokenExpiry
	h.ClientIDHex = vectorClientHex
	h.UnixTimeSeconds = vectorUnixTime

	// Carried across, and minted only when there is nothing to carry. See the note above.
	h.TokenHex = old.HandshakeReqToken.TokenHex
	if h.TokenHex == "" {
		tok, err := BuildToken(licencePriv, vectorTokenUser,
			MarshalPublicKey(&devicePriv.PublicKey), time.Unix(vectorTokenExpiry, 0),
			vectorTokenTier, vectorTokenMaxSess)
		if err != nil {
			t.Fatalf("mint the vector token: %v", err)
		}
		h.TokenHex = hex.EncodeToString(tok)
		t.Log("minted a NEW vector token - .NET's packet must be re-emitted or it will not match")
	}

	goReq, _, err := BuildHandshakeReqToken(devicePriv, mustHex(t, h.TokenHex), cid, now)
	if err != nil {
		t.Fatalf("build the token handshake: %v", err)
	}
	h.PacketFromGoHex = hex.EncodeToString(goReq)

	h.PacketFromDotnetHex = old.HandshakeReqToken.PacketFromDotnetHex
	if h.PacketFromDotnetHex == "" {
		t.Log("no .NET token handshake carried over - produce one and paste it in, or the " +
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

	checkTokenHandshake(t, v)
}

// checkTokenHandshake runs the 240-byte v3 token HandshakeReq through the SAME function relayd
// calls, in both directions.
//
// Verifying .NET's packet is the half that matters and the reason this exists at all. Go
// verifying its own packet proves only that Go is self-consistent, which it would be even if
// every offset in the format had moved.
func checkTokenHandshake(t *testing.T, v *vectorFile) {
	t.Helper()
	h := &v.HandshakeReqToken

	// The frozen instant everything here is judged against. Using time.Now() would make the
	// clock-skew check fail a minute after the file was written, and the expiry check fail a day
	// after - a test that rots rather than one that catches drift.
	now := time.Unix(h.UnixTimeSeconds, 0)

	licencePriv, err := ParsePrivateKey(mustHex(t, h.LicencePrivateKeyHex))
	if err != nil {
		t.Fatalf("the committed licence key does not parse: %v", err)
	}
	if got := hex.EncodeToString(MarshalPublicKey(&licencePriv.PublicKey)); got != h.LicencePublicKeyHex {
		t.Errorf("licence public key derived from the committed scalar: got %s, want %s",
			got, h.LicencePublicKeyHex)
	}
	licencePub, err := ParsePublicKey(mustHex(t, h.LicencePublicKeyHex))
	if err != nil {
		t.Fatalf("the committed licence public key does not parse: %v", err)
	}

	// The token on its own, before any packet is involved. If this drifts, both packets fail and
	// the reason would otherwise be hard to see.
	tok, err := VerifyToken(licencePub, mustHex(t, h.TokenHex), now)
	if err != nil {
		t.Fatalf("the committed token no longer verifies: %v", err)
	}
	if tok.UserID != h.UserID {
		t.Errorf("token user id is %d, want %d", tok.UserID, h.UserID)
	}
	if got := hex.EncodeToString(tok.DeviceKeyRaw()); got != h.DevicePublicKeyHex {
		t.Errorf("token names device key: got %s, want %s", got, h.DevicePublicKeyHex)
	}
	if tok.Expiry.Unix() != h.ExpiryUnixSeconds {
		t.Errorf("token expiry is %d, want %d", tok.Expiry.Unix(), h.ExpiryUnixSeconds)
	}

	var wantID ClientID
	copy(wantID[:], mustHex(t, h.ClientIDHex))

	verify := func(label, packetHex string) {
		pkt := mustHex(t, packetHex)
		if len(pkt) != HandshakeReqTokenLen {
			t.Errorf("%s: packet is %d bytes, want %d", label, len(pkt), HandshakeReqTokenLen)
			return
		}
		if pkt[hsOffMode] != AuthModeToken {
			t.Errorf("%s: auth mode byte is %d, want AuthModeToken", label, pkt[hsOffMode])
		}

		// The whole point: the same call relayd makes. It checks the header, the version, the
		// type, the auth mode, the token's signature by the LICENCE key, the request's signature
		// by the DEVICE key the token names, and the clock skew.
		gotTok, gotID, _, err := VerifyHandshakeReqToken(licencePub, pkt, now)
		if err != nil {
			t.Errorf("%s: a relay would REJECT this handshake: %v", label, err)
			return
		}
		if gotID != wantID {
			t.Errorf("%s: client id read as %x, want %x", label, gotID, wantID)
		}
		if gotTok.UserID != h.UserID {
			t.Errorf("%s: user id read as %d, want %d", label, gotTok.UserID, h.UserID)
		}
		if got := hex.EncodeToString(gotTok.DeviceKeyRaw()); got != h.DevicePublicKeyHex {
			t.Errorf("%s: device key read as %s, want %s", label, got, h.DevicePublicKeyHex)
		}

		// A test that only ever accepts is a test that cannot fail. Flip one bit of the signed
		// span and the same call must refuse it - otherwise the acceptance above means nothing.
		tampered := append([]byte(nil), pkt...)
		tampered[hsOffClientID] ^= 0x01
		if _, _, _, err := VerifyHandshakeReqToken(licencePub, tampered, now); err == nil {
			t.Errorf("%s: a packet with one flipped bit was ACCEPTED", label)
		}
	}

	verify("packet built by Go", h.PacketFromGoHex)

	if h.PacketFromDotnetHex == "" {
		t.Error("no .NET token handshake in the vectors: the cross-language check is not " +
			"running, which is worse than it failing, because it looks like it passed")
		return
	}
	verify("packet built by .NET", h.PacketFromDotnetHex)

	// The two packets must differ. ECDSA is randomised and each side picks its own nonce, so
	// identical bytes would mean the file was generated wrongly - most likely .NET's slot
	// holding a copy of Go's packet, which would make the cross-language check verify Go's own
	// output under a label that says otherwise.
	if h.PacketFromGoHex == h.PacketFromDotnetHex {
		t.Error("the Go and .NET packets are byte-identical, so one of them is not what it claims")
	}
}
