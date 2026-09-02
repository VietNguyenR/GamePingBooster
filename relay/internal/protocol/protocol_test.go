package protocol

import (
	"bytes"
	"net/netip"
	"testing"
	"time"
)

var psk = []byte("test-psk-0123456789abcdef")

func TestHandshakeRoundTrip(t *testing.T) {
	now := time.Now()
	clientID := ClientID{1, 2, 3, 4, 5, 6, 7, 8}

	req, nonce, err := BuildHandshakeReq(psk, clientID, now)
	if err != nil {
		t.Fatalf("BuildHandshakeReq: %v", err)
	}
	if len(req) != HandshakeReqPSKLen {
		t.Fatalf("HandshakeReq length = %d, want %d", len(req), HandshakeReqPSKLen)
	}
	if req[1] != AuthModePSK {
		t.Fatalf("auth mode byte = %d, want AuthModePSK", req[1])
	}

	gotID, gotNonce, err := VerifyHandshakeReq(psk, req, now)
	if err != nil {
		t.Fatalf("VerifyHandshakeReq: %v", err)
	}
	if gotID != clientID {
		t.Fatalf("client id = %v, want %v", gotID, clientID)
	}
	if gotNonce != nonce {
		t.Fatalf("nonce read back as %x, want %x", gotNonce, nonce)
	}

	sid := SessionID{1, 2, 3, 4, 5, 6, 7, 8}
	clientIP := netip.MustParseAddr("10.77.0.5")
	relayIP := netip.MustParseAddr("10.77.0.1")
	resp := BuildHandshakeResp(psk, StatusOK, sid, clientIP, relayIP, 1400, nonce)
	if len(resp) != HandshakeRespPSKLen {
		t.Fatalf("HandshakeResp length = %d, want %d", len(resp), HandshakeRespPSKLen)
	}

	got, err := ParseHandshakeResp(psk, resp, nonce)
	if err != nil {
		t.Fatalf("ParseHandshakeResp: %v", err)
	}
	if got.Status != StatusOK || got.Session != sid || got.ClientIP != clientIP ||
		got.RelayIP != relayIP || got.MTU != 1400 {
		t.Fatalf("wrong handshake result: %+v", got)
	}
}

func TestHandshakeRejectsBadKeyAndSkew(t *testing.T) {
	now := time.Now()
	req, _, _ := BuildHandshakeReq(psk, ClientID{9}, now)

	if _, _, err := VerifyHandshakeReq([]byte("wrong-key-wrong-key-wrong"), req, now); err != ErrBadAuth {
		t.Fatalf("wrong PSK should return ErrBadAuth, got %v", err)
	}
	if _, _, err := VerifyHandshakeReq(psk, req, now.Add(5*time.Minute)); err != ErrClockSkew {
		t.Fatalf("clock skew should return ErrClockSkew, got %v", err)
	}

	// The client id is inside the signed range, so tampering with it must break the HMAC.
	// Offset 21 is inside the client id now that the auth-mode byte shifted everything by one.
	tampered := append([]byte(nil), req...)
	tampered[21] ^= 0x01
	if _, _, err := VerifyHandshakeReq(psk, tampered, now); err != ErrBadAuth {
		t.Fatalf("tampered client id should return ErrBadAuth, got %v", err)
	}

	// The auth mode is inside the signed range too, so a token-mode relay cannot be fed a
	// PSK-mode packet with the byte flipped.
	tampered = append([]byte(nil), req...)
	tampered[1] = AuthModeToken
	if _, _, err := VerifyHandshakeReq(psk, tampered, now); err != ErrBadAuthMode {
		t.Fatalf("a flipped auth mode should return ErrBadAuthMode, got %v", err)
	}
}

// The answer must be tied to the request that asked for it. Without the echo, a captured
// response replayed at a client that is mid-handshake hands it a session id the relay has
// already forgotten, and the tunnel comes up carrying nothing until the idle timeout. v2 had
// exactly this hole.
func TestHandshakeRespMustEchoTheNonce(t *testing.T) {
	sid := SessionID{1}
	clientIP := netip.MustParseAddr("10.77.0.5")
	relayIP := netip.MustParseAddr("10.77.0.1")

	sent := [8]byte{1, 2, 3, 4, 5, 6, 7, 8}
	other := [8]byte{8, 7, 6, 5, 4, 3, 2, 1}

	resp := BuildHandshakeResp(psk, StatusOK, sid, clientIP, relayIP, 1400, sent)

	if _, err := ParseHandshakeResp(psk, resp, sent); err != nil {
		t.Fatalf("the matching nonce was rejected: %v", err)
	}
	if _, err := ParseHandshakeResp(psk, resp, other); err != ErrBadNonce {
		t.Fatalf("a reply to somebody else's handshake was accepted, got %v", err)
	}
}

func TestVersionMismatchResponseIsReadableByTheOtherVersion(t *testing.T) {
	// The whole point of this message is that a client on a different version can still parse it.
	// The reply therefore carries the client's version, not ours.
	const clientVersion = 1
	resp := BuildVersionMismatchResp(psk, clientVersion)

	// The V2 layout, on purpose: an old client can only parse what it already knows.
	if len(resp) != HandshakeRespV2Len {
		t.Fatalf("length = %d, want the v2 layout's %d", len(resp), HandshakeRespV2Len)
	}
	v, msgType := ParseHeader(resp[0])
	if v != clientVersion {
		t.Fatalf("header version = %d, want the client's %d", v, clientVersion)
	}
	if msgType != TypeHandshakeResp {
		t.Fatalf("type = %d, want TypeHandshakeResp", msgType)
	}
	if resp[1] != StatusVersionMismatch {
		t.Fatalf("status = %d, want StatusVersionMismatch", resp[1])
	}

	// Our own parser rejects it precisely because the version is not ours - which is correct,
	// and is why the field has to be read before the version check on the receiving side.
	if _, err := ParseHandshakeResp(psk, resp, [8]byte{}); err != ErrShortPacket {
		// Under v3 it fails on length before it ever reaches the version check, because the v2
		// layout is 52 bytes and v3's is 60. Either refusal is correct; what matters is that a
		// v3 client never mistakes this for an answer meant for it.
		t.Fatalf("expected ErrShortPacket from our own parser, got %v", err)
	}
}

func TestDataRoundTrip(t *testing.T) {
	sid := SessionID{9, 9, 9, 9, 9, 9, 9, 9}
	// Minimal IPv4 packet: version 4, IHL 5, src 10.77.0.5, dst 52.220.1.1.
	ip := make([]byte, 28)
	ip[0] = 0x45
	copy(ip[12:16], []byte{10, 77, 0, 5})
	copy(ip[16:20], []byte{52, 220, 1, 1})
	copy(ip[20:], []byte("payload!"))

	buf := make([]byte, MaxPacketLen)
	wire := EncodeData(buf, sid, ip)
	if len(wire) != DataHeaderLen+len(ip) {
		t.Fatalf("Data packet length = %d, want %d", len(wire), DataHeaderLen+len(ip))
	}

	gotSid, payload, err := DecodeData(wire)
	if err != nil {
		t.Fatalf("DecodeData: %v", err)
	}
	if gotSid != sid {
		t.Fatalf("session id = %v, want %v", gotSid, sid)
	}
	if !bytes.Equal(payload, ip) {
		t.Fatal("decoded payload does not match the original packet")
	}

	src, _ := SrcIPv4(payload)
	dst, _ := DstIPv4(payload)
	if src != netip.MustParseAddr("10.77.0.5") || dst != netip.MustParseAddr("52.220.1.1") {
		t.Fatalf("wrong addresses: src=%v dst=%v", src, dst)
	}
}

func TestDecodeDataRejectsNonIPv4(t *testing.T) {
	buf := make([]byte, MaxPacketLen)
	v6ish := make([]byte, 40)
	v6ish[0] = 0x60 // version 6
	wire := EncodeData(buf, SessionID{}, v6ish)
	if _, _, err := DecodeData(wire); err != ErrNotIPv4 {
		t.Fatalf("non-IPv4 packet should return ErrNotIPv4, got %v", err)
	}
	if _, _, err := DecodeData(wire[:DataHeaderLen]); err != ErrShortPacket {
		t.Fatalf("empty packet should return ErrShortPacket, got %v", err)
	}
}

func TestPingRoundTrip(t *testing.T) {
	sid := SessionID{7}
	const stamp = uint64(1234567890)
	ping := BuildPing(sid, stamp)
	if _, msgType := ParseHeader(ping[0]); msgType != TypePing {
		t.Fatalf("type = %d, want TypePing", msgType)
	}
	gotSid, gotStamp, err := DecodePing(ping)
	if err != nil || gotSid != sid || gotStamp != stamp {
		t.Fatalf("DecodePing = (%v, %d, %v)", gotSid, gotStamp, err)
	}

	pong := BuildPong(sid, stamp)
	if _, msgType := ParseHeader(pong[0]); msgType != TypePong {
		t.Fatalf("type = %d, want TypePong", msgType)
	}
}
