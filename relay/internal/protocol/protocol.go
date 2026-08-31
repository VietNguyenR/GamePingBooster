// Package protocol defines the wire format between the Windows client and the relay.
// The spec lives in docs/PROTOCOL.md - changing this file means changing the spec and
// the C# mirror in client/src/GamePingBooster.Core/Protocol/ as well.
package protocol

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/binary"
	"errors"
	"net/netip"
	"time"
)

const (
	Version = 2

	TypeHandshakeReq  = 0x1
	TypeHandshakeResp = 0x2
	TypeData          = 0x3
	TypePing          = 0x4
	TypePong          = 0x5
	TypeDisconnect    = 0x6
)

// Fixed sizes for each message type (see docs/PROTOCOL.md).
const (
	// HandshakeReqLen grew from 49 to 57 in v2 with the addition of the client id.
	HandshakeReqLen  = 57
	HandshakeRespLen = 52
	DataHeaderLen    = 9
	PingLen          = 17
	DisconnectLen    = 9

	// MaxPacketLen: max virtual adapter MTU of 1500 plus our header, rounded up.
	MaxPacketLen = 2048

	// HandshakeSkew is the accepted clock drift window, a coarse replay guard.
	HandshakeSkew = 120 * time.Second
)

// Status codes carried in HandshakeResp.
const (
	StatusOK              = 0
	StatusPoolFull        = 1
	StatusShutdown        = 2
	StatusVersionMismatch = 3
)

var (
	ErrShortPacket = errors.New("packet too short")
	ErrBadVersion  = errors.New("wrong protocol version")
	ErrBadType     = errors.New("wrong message type")
	ErrBadAuth     = errors.New("invalid HMAC")
	ErrClockSkew   = errors.New("timestamp too far out of range")
	ErrNotIPv4     = errors.New("payload is not an IPv4 packet")
)

// ClientID identifies a client across reconnects so the relay can hand back the same inner IP.
// It is random, generated once per installation, and carries no personal information - its only
// job is to let a returning client keep its address so the routing table does not have to be
// rebuilt on every blip.
type ClientID [8]byte

// SessionID is the 8-byte identifier the relay assigns after a handshake.
type SessionID [8]byte

func header(msgType byte) byte { return Version<<4 | msgType&0x0f }

// ParseHeader splits version and type out of the first byte.
func ParseHeader(b byte) (version, msgType byte) { return b >> 4, b & 0x0f }

func sign(psk, data []byte) []byte {
	m := hmac.New(sha256.New, psk)
	m.Write(data)
	return m.Sum(nil)
}

// ---------------------------------------------------------------- Handshake

// BuildHandshakeReq builds a signed HandshakeReq. It also returns the nonce so the
// client can log it (the relay does not echo it back).
func BuildHandshakeReq(psk []byte, clientID ClientID, now time.Time) (pkt []byte, nonce [8]byte, err error) {
	if _, err = rand.Read(nonce[:]); err != nil {
		return nil, nonce, err
	}
	pkt = make([]byte, HandshakeReqLen)
	pkt[0] = header(TypeHandshakeReq)
	copy(pkt[1:9], nonce[:])
	binary.BigEndian.PutUint64(pkt[9:17], uint64(now.Unix()))
	copy(pkt[17:25], clientID[:])
	copy(pkt[25:], sign(psk, pkt[:25]))
	return pkt, nonce, nil
}

// VerifyHandshakeReq checks the HMAC and the clock skew, and returns the client id.
func VerifyHandshakeReq(psk, pkt []byte, now time.Time) (ClientID, error) {
	var id ClientID
	if len(pkt) != HandshakeReqLen {
		return id, ErrShortPacket
	}
	v, t := ParseHeader(pkt[0])
	if v != Version {
		return id, ErrBadVersion
	}
	if t != TypeHandshakeReq {
		return id, ErrBadType
	}
	if !hmac.Equal(pkt[25:], sign(psk, pkt[:25])) {
		return id, ErrBadAuth
	}
	ts := time.Unix(int64(binary.BigEndian.Uint64(pkt[9:17])), 0)
	if d := now.Sub(ts); d > HandshakeSkew || d < -HandshakeSkew {
		return id, ErrClockSkew
	}
	copy(id[:], pkt[17:25])
	return id, nil
}

// BuildVersionMismatchResp answers a handshake from a client speaking a different protocol
// version. The reply deliberately carries the CLIENT's version in its header, not ours: a client
// that cannot parse the answer learns nothing, and silence is indistinguishable from a dead
// relay or a blocked port. The HandshakeResp layout has not changed since v1, so a v1 client
// parses this and reports a refusal instead of timing out.
func BuildVersionMismatchResp(psk []byte, clientVersion byte) []byte {
	pkt := make([]byte, HandshakeRespLen)
	pkt[0] = clientVersion<<4 | TypeHandshakeResp
	pkt[1] = StatusVersionMismatch
	copy(pkt[20:], sign(psk, pkt[:20]))
	return pkt
}

// BuildHandshakeResp packs the handshake result and signs it with the PSK.
func BuildHandshakeResp(psk []byte, status byte, sid SessionID, clientIP, relayIP netip.Addr, mtu uint16) []byte {
	pkt := make([]byte, HandshakeRespLen)
	pkt[0] = header(TypeHandshakeResp)
	pkt[1] = status
	copy(pkt[2:10], sid[:])
	if clientIP.Is4() {
		a := clientIP.As4()
		copy(pkt[10:14], a[:])
	}
	if relayIP.Is4() {
		a := relayIP.As4()
		copy(pkt[14:18], a[:])
	}
	binary.BigEndian.PutUint16(pkt[18:20], mtu)
	copy(pkt[20:], sign(psk, pkt[:20]))
	return pkt
}

// HandshakeResult is the content of a HandshakeResp once the client has verified it.
type HandshakeResult struct {
	Status   byte
	Session  SessionID
	ClientIP netip.Addr
	RelayIP  netip.Addr
	MTU      uint16
}

// ParseHandshakeResp is used on the client side (and in tests).
func ParseHandshakeResp(psk, pkt []byte) (HandshakeResult, error) {
	var r HandshakeResult
	if len(pkt) != HandshakeRespLen {
		return r, ErrShortPacket
	}
	if v, t := ParseHeader(pkt[0]); v != Version {
		return r, ErrBadVersion
	} else if t != TypeHandshakeResp {
		return r, ErrBadType
	}
	if !hmac.Equal(pkt[20:], sign(psk, pkt[:20])) {
		return r, ErrBadAuth
	}
	r.Status = pkt[1]
	copy(r.Session[:], pkt[2:10])
	r.ClientIP = netip.AddrFrom4([4]byte(pkt[10:14]))
	r.RelayIP = netip.AddrFrom4([4]byte(pkt[14:18]))
	r.MTU = binary.BigEndian.Uint16(pkt[18:20])
	return r, nil
}

// ------------------------------------------------------------------- Data

// EncodeData writes the Data header plus the IP packet into buf (which must be large
// enough) and returns the used slice. It allocates nothing - this is the hot path.
func EncodeData(buf []byte, sid SessionID, ipPacket []byte) []byte {
	buf[0] = header(TypeData)
	copy(buf[1:9], sid[:])
	n := copy(buf[DataHeaderLen:], ipPacket)
	return buf[:DataHeaderLen+n]
}

// DecodeData splits out the session id and the payload. The payload aliases pkt; it is
// not copied.
func DecodeData(pkt []byte) (SessionID, []byte, error) {
	var sid SessionID
	if len(pkt) <= DataHeaderLen {
		return sid, nil, ErrShortPacket
	}
	copy(sid[:], pkt[1:9])
	payload := pkt[DataHeaderLen:]
	if payload[0]>>4 != 4 {
		return sid, nil, ErrNotIPv4
	}
	return sid, payload, nil
}

// ------------------------------------------------------------- Ping / Pong

func BuildPing(sid SessionID, stamp uint64) []byte { return buildPingLike(TypePing, sid, stamp) }
func BuildPong(sid SessionID, stamp uint64) []byte { return buildPingLike(TypePong, sid, stamp) }

func buildPingLike(t byte, sid SessionID, stamp uint64) []byte {
	pkt := make([]byte, PingLen)
	pkt[0] = header(t)
	copy(pkt[1:9], sid[:])
	binary.BigEndian.PutUint64(pkt[9:17], stamp)
	return pkt
}

// DecodePing reads the session id and timestamp out of a Ping/Pong message.
func DecodePing(pkt []byte) (SessionID, uint64, error) {
	var sid SessionID
	if len(pkt) != PingLen {
		return sid, 0, ErrShortPacket
	}
	copy(sid[:], pkt[1:9])
	return sid, binary.BigEndian.Uint64(pkt[9:17]), nil
}

// ------------------------------------------------------------- Disconnect

func BuildDisconnect(sid SessionID) []byte {
	pkt := make([]byte, DisconnectLen)
	pkt[0] = header(TypeDisconnect)
	copy(pkt[1:9], sid[:])
	return pkt
}

// DecodeSessionID reads the session id out of any Data/Ping/Pong/Disconnect message.
func DecodeSessionID(pkt []byte) (SessionID, error) {
	var sid SessionID
	if len(pkt) < 9 {
		return sid, ErrShortPacket
	}
	copy(sid[:], pkt[1:9])
	return sid, nil
}

// SrcIPv4 and DstIPv4 read the addresses out of an IPv4 header (offsets 12 and 16).
func SrcIPv4(ipPacket []byte) (netip.Addr, bool) {
	if len(ipPacket) < 20 {
		return netip.Addr{}, false
	}
	return netip.AddrFrom4([4]byte(ipPacket[12:16])), true
}

func DstIPv4(ipPacket []byte) (netip.Addr, bool) {
	if len(ipPacket) < 20 {
		return netip.Addr{}, false
	}
	return netip.AddrFrom4([4]byte(ipPacket[16:20])), true
}
