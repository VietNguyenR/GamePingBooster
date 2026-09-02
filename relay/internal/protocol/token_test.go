package protocol

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/sha256"
	"net/netip"
	"testing"
	"time"
)

// tokenFixture builds the three keys a licensed handshake involves, so each test does not have
// to repeat the setup: the licence server's, the client device's, and the relay's own. They are
// three separate keys on purpose - a test that reused one would pass while the code confused
// them, which is the mistake most worth catching here.
type tokenFixture struct {
	licenceKey *ecdsa.PrivateKey
	deviceKey  *ecdsa.PrivateKey
	relayKey   *ecdsa.PrivateKey
	token      []byte
	now        time.Time
}

func mustAddr(s string) netip.Addr { return netip.MustParseAddr(s) }

func newTokenFixture(t *testing.T, expiry time.Duration) *tokenFixture {
	t.Helper()
	licence, err := GenerateKey()
	if err != nil {
		t.Fatalf("licence key: %v", err)
	}
	device, err := GenerateKey()
	if err != nil {
		t.Fatalf("device key: %v", err)
	}
	relay, err := GenerateKey()
	if err != nil {
		t.Fatalf("relay key: %v", err)
	}
	now := time.Unix(1767225600, 0) // 2026-01-01, fixed so nothing depends on the wall clock

	tok, err := BuildToken(licence, 4242, MarshalPublicKey(&device.PublicKey),
		now.Add(expiry), 1, 0)
	if err != nil {
		t.Fatalf("BuildToken: %v", err)
	}
	if len(tok) != TokenLen {
		t.Fatalf("token is %d bytes, want %d", len(tok), TokenLen)
	}

	f := &tokenFixture{token: tok, now: now}
	f.licenceKey, f.deviceKey, f.relayKey = licence, device, relay
	return f
}

// The whole licensed path, end to end: the licence server mints a token, the client signs a
// handshake with the device key the token names, the relay verifies both without any network
// call, and answers with a signature the client checks against the relay key it got from the
// profile.
func TestTokenHandshakeRoundTrip(t *testing.T) {
	f := newTokenFixture(t, 24*time.Hour)

	clientID := ClientID{1, 2, 3, 4, 5, 6, 7, 8}
	req, nonce, err := BuildHandshakeReqToken(f.deviceKey, f.token, clientID, f.now)
	if err != nil {
		t.Fatalf("BuildHandshakeReqToken: %v", err)
	}
	if len(req) != HandshakeReqTokenLen {
		t.Fatalf("request is %d bytes, want %d", len(req), HandshakeReqTokenLen)
	}
	if req[1] != AuthModeToken {
		t.Fatalf("auth mode byte is %d, want AuthModeToken", req[1])
	}

	tok, gotID, gotNonce, err := VerifyHandshakeReqToken(&f.licenceKey.PublicKey, req, f.now)
	if err != nil {
		t.Fatalf("the relay rejected a valid handshake: %v", err)
	}
	if tok.UserID != 4242 {
		t.Errorf("user id read as %d, want 4242", tok.UserID)
	}
	if gotID != clientID {
		t.Errorf("client id read as %x, want %x", gotID, clientID)
	}
	if gotNonce != nonce {
		t.Errorf("nonce read as %x, want %x", gotNonce, nonce)
	}
	if !bytes.Equal(tok.DeviceKeyRaw(), MarshalPublicKey(&f.deviceKey.PublicKey)) {
		t.Error("the device key the relay read is not the one the token was minted for")
	}

	sid := SessionID{9, 9, 9, 9, 9, 9, 9, 9}
	resp, err := BuildHandshakeRespToken(f.relayKey, StatusOK, sid,
		mustAddr("10.77.0.5"), mustAddr("10.77.0.1"), 1400, nonce)
	if err != nil {
		t.Fatalf("BuildHandshakeRespToken: %v", err)
	}
	if len(resp) != HandshakeRespTokenLen {
		t.Fatalf("answer is %d bytes, want %d", len(resp), HandshakeRespTokenLen)
	}

	res, err := ParseHandshakeRespToken(&f.relayKey.PublicKey, resp, nonce)
	if err != nil {
		t.Fatalf("the client rejected a valid answer: %v", err)
	}
	if res.Session != sid || res.MTU != 1400 {
		t.Errorf("wrong result: %+v", res)
	}
}

// A token is worthless without the device private key it names. This is what stops a token
// copied off somebody's disk from working anywhere else.
func TestStolenTokenIsUselessWithoutTheDeviceKey(t *testing.T) {
	f := newTokenFixture(t, 24*time.Hour)

	thief, err := GenerateKey()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}

	req, _, err := BuildHandshakeReqToken(thief, f.token, ClientID{1}, f.now)
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	if _, _, _, err := VerifyHandshakeReqToken(&f.licenceKey.PublicKey, req, f.now); err != ErrBadAuth {
		t.Fatalf("a token presented without its device key was accepted, got %v", err)
	}
}

// An expired token is the ONE failure the relay answers rather than dropping in silence: the
// signature verified, so this is a real customer whose subscription lapsed, not a stranger
// probing the port. The token comes back with the error so the relay can log who it was.
func TestExpiredTokenIsRefusedButIdentified(t *testing.T) {
	f := newTokenFixture(t, -time.Minute) // already expired

	req, _, err := BuildHandshakeReqToken(f.deviceKey, f.token, ClientID{1}, f.now)
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	tok, _, nonce, err := VerifyHandshakeReqToken(&f.licenceKey.PublicKey, req, f.now)
	if err != ErrTokenExpired {
		t.Fatalf("expected ErrTokenExpired, got %v", err)
	}
	if tok == nil {
		t.Fatal("no token came back, so the relay cannot log which customer was refused")
	}
	if tok.UserID != 4242 {
		t.Errorf("user id read as %d, want 4242", tok.UserID)
	}
	if nonce == ([8]byte{}) {
		t.Error("no nonce came back, so the refusal cannot be bound to this request")
	}
}

// A token signed by anybody else is not a token. Without this check the relay would take
// entitlement claims from whoever asked.
func TestTokenFromAnotherLicenceKeyIsRefused(t *testing.T) {
	f := newTokenFixture(t, 24*time.Hour)

	impostor, err := GenerateKey()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	forged, err := BuildToken(impostor, 1, MarshalPublicKey(&f.deviceKey.PublicKey),
		f.now.Add(time.Hour), 9, 0)
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	if _, err := VerifyToken(&f.licenceKey.PublicKey, forged, f.now); err != ErrBadTokenSig {
		t.Fatalf("a token signed by somebody else was accepted, got %v", err)
	}
}

// Every byte of the token is covered by its signature. Flipping any one of them must break it -
// otherwise a field could be edited in transit, and expiry and tier are exactly the fields
// somebody would want to edit.
func TestEveryTokenByteIsSigned(t *testing.T) {
	f := newTokenFixture(t, 24*time.Hour)

	for i := 0; i < tokenOffSignature; i++ {
		tampered := bytes.Clone(f.token)
		tampered[i] ^= 0x01
		if _, err := VerifyToken(&f.licenceKey.PublicKey, tampered, f.now); err == nil {
			t.Fatalf("byte %d can be changed without breaking the signature", i)
		}
	}
}

// The reservation key the relay derives has to be stable for one device and different between
// devices - it is what replaces the client-chosen client id in licensed mode, and the reason one
// customer cannot take over another's inner address.
func TestReservationKeyFollowsTheDeviceNotTheClientID(t *testing.T) {
	f := newTokenFixture(t, 24*time.Hour)

	other, err := GenerateKey()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	otherTok, err := BuildToken(f.licenceKey, 4242, MarshalPublicKey(&other.PublicKey),
		f.now.Add(time.Hour), 1, 0)
	if err != nil {
		t.Fatalf("build: %v", err)
	}

	a, err := VerifyToken(&f.licenceKey.PublicKey, f.token, f.now)
	if err != nil {
		t.Fatalf("verify: %v", err)
	}
	b, err := VerifyToken(&f.licenceKey.PublicKey, otherTok, f.now)
	if err != nil {
		t.Fatalf("verify: %v", err)
	}

	keyA := sha256.Sum256(a.DeviceKeyRaw())
	keyB := sha256.Sum256(b.DeviceKeyRaw())
	if bytes.Equal(keyA[:8], keyB[:8]) {
		t.Error("two devices of the same user derive the same reservation key - they would fight over one address")
	}

	again, err := VerifyToken(&f.licenceKey.PublicKey, f.token, f.now)
	if err != nil {
		t.Fatalf("verify: %v", err)
	}
	keyAgain := sha256.Sum256(again.DeviceKeyRaw())
	if !bytes.Equal(keyA[:8], keyAgain[:8]) {
		t.Error("the same device derives a different reservation key each time - it would never resume its address")
	}
}
