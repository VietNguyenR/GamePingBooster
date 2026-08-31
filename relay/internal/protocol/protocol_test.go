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
	req, _, err := BuildHandshakeReq(psk, now)
	if err != nil {
		t.Fatalf("BuildHandshakeReq: %v", err)
	}
	if len(req) != HandshakeReqLen {
		t.Fatalf("HandshakeReq length = %d, want %d", len(req), HandshakeReqLen)
	}
	if err := VerifyHandshakeReq(psk, req, now); err != nil {
		t.Fatalf("VerifyHandshakeReq: %v", err)
	}

	sid := SessionID{1, 2, 3, 4, 5, 6, 7, 8}
	clientIP := netip.MustParseAddr("10.77.0.5")
	relayIP := netip.MustParseAddr("10.77.0.1")
	resp := BuildHandshakeResp(psk, StatusOK, sid, clientIP, relayIP, 1400)
	if len(resp) != HandshakeRespLen {
		t.Fatalf("HandshakeResp length = %d, want %d", len(resp), HandshakeRespLen)
	}

	got, err := ParseHandshakeResp(psk, resp)
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
	req, _, _ := BuildHandshakeReq(psk, now)

	if err := VerifyHandshakeReq([]byte("wrong-key-wrong-key-wrong"), req, now); err != ErrBadAuth {
		t.Fatalf("wrong PSK should return ErrBadAuth, got %v", err)
	}
	if err := VerifyHandshakeReq(psk, req, now.Add(5*time.Minute)); err != ErrClockSkew {
		t.Fatalf("clock skew should return ErrClockSkew, got %v", err)
	}

	// Flipping a single bit in the payload must break the HMAC.
	tampered := append([]byte(nil), req...)
	tampered[3] ^= 0x01
	if err := VerifyHandshakeReq(psk, tampered, now); err != ErrBadAuth {
		t.Fatalf("tampered packet should return ErrBadAuth, got %v", err)
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
