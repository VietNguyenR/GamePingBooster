// Package protocol defines the wire format between the Windows client and the relay.
// The spec lives in docs/PROTOCOL.md - changing this file means changing the spec and
// the C# mirror in client/src/GamePingBooster.Core/Protocol/ as well.
package protocol

import (
	"crypto/ecdsa"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/binary"
	"errors"
	"net/netip"
	"time"
)

const (
	Version = 3

	TypeHandshakeReq  = 0x1
	TypeHandshakeResp = 0x2
	TypeData          = 0x3
	TypePing          = 0x4
	TypePong          = 0x5
	TypeDisconnect    = 0x6

	// TypeDataEncrypted is RESERVED and must never be sent or accepted. Data is deliberately
	// plaintext - see docs/PROTOCOL-v3.md and HANDOFF section 7. Reserving the number now costs
	// nothing and means encryption can be added later beside the plaintext path instead of
	// forcing a second handshake redesign.
	TypeDataEncrypted = 0x7
)

// Authentication modes. A relay is configured for exactly one and answers only that one.
const (
	// AuthModePSK is the self-hosted mode: one shared key, as in v1 and v2.
	AuthModePSK = 0
	// AuthModeToken is the commercial mode: a licence token signed by the licence server, which
	// the relay verifies offline against a public key.
	AuthModeToken = 1
)

// Fixed sizes for each message type (see docs/PROTOCOL-v3.md).
//
// There is no length field anywhere: each authentication mode has its own fixed layout, and the
// side reading a packet already knows which mode it is in. That keeps the format free of TLV,
// which is the same reason the rest of it is fixed.
const (
	// HandshakeReqPSKLen is v2's 57 bytes plus the auth-mode byte.
	HandshakeReqPSKLen = 58
	// HandshakeReqTokenLen carries the licence token and a device signature instead of an HMAC.
	HandshakeReqTokenLen = 240

	// HandshakeRespPSKLen is v2's 52 plus the mode byte and the nonce echo.
	HandshakeRespPSKLen = 60
	// HandshakeRespTokenLen swaps the 32-byte HMAC for a 64-byte relay signature.
	HandshakeRespTokenLen = 92

	// HandshakeRespV2Len is the v2 layout, kept ONLY so a v1 or v2 client can still parse a
	// version-mismatch refusal. Nothing else may use it.
	HandshakeRespV2Len = 52

	DataHeaderLen = 9
	PingLen       = 17
	DisconnectLen = 9

	// MaxPacketLen: max virtual adapter MTU of 1500 plus our header, rounded up.
	MaxPacketLen = 2048

	// HandshakeSkew is the accepted clock drift window, a coarse replay guard.
	HandshakeSkew = 120 * time.Second
)

// Status codes carried in HandshakeResp.
//
// StatusCredentialExpired and StatusCredentialRevoked are sent ONLY after the signature has
// verified. Telling an unauthenticated stranger why they were refused would turn the relay into
// an oracle; telling a real customer is the difference between a useful message and a timeout.
const (
	StatusOK                = 0
	StatusPoolFull          = 1
	StatusShutdown          = 2
	StatusVersionMismatch   = 3
	StatusCredentialExpired = 4
	StatusCredentialRevoked = 5
)

// MaxSessionAge caps how long one handshake is good for.
//
// A session is authenticated once, at handshake, and never re-checked while it runs: cutting a
// customer off mid-match is the worst possible moment, and a lapsed subscription is refused at
// the next connect anyway. The cost of that choice is a session held open forever, which this
// closes. No real game session lasts a day.
const MaxSessionAge = 24 * time.Hour

var (
	ErrShortPacket = errors.New("packet too short")
	ErrBadVersion  = errors.New("wrong protocol version")
	ErrBadType     = errors.New("wrong message type")
	ErrBadAuth     = errors.New("invalid HMAC")
	ErrClockSkew   = errors.New("timestamp too far out of range")
	ErrNotIPv4     = errors.New("payload is not an IPv4 packet")
	ErrBadAuthMode = errors.New("handshake is for a different authentication mode")
	ErrBadNonce    = errors.New("the answer does not echo the nonce that was sent")
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
//
// Two layouts, chosen by the auth-mode byte at offset 1. Both start with the same nine bytes so
// a relay can read the mode before deciding how to parse the rest.
//
// Common prefix, both modes:
//
//	off  len  field
//	0    1    header
//	1    1    auth mode
//	2    8    client nonce
//	10   8    unix timestamp
//	18   8    client id
//
// PSK mode then has 32 bytes of HMAC over bytes[0..26).
// Token mode has a 150-byte licence token and 64 bytes of device signature over bytes[0..176).

const (
	hsOffMode      = 1
	hsOffNonce     = 2
	hsOffTime      = 10
	hsOffClientID  = 18
	hsOffAuthStart = 26 // where the credential begins in either mode

	hsTokenSigStart = hsOffAuthStart + TokenLen // 176
)

// BuildHandshakeReq builds a PSK-mode HandshakeReq. It also returns the nonce, which the client
// keeps to check against the echo in the answer.
func BuildHandshakeReq(psk []byte, clientID ClientID, now time.Time) (pkt []byte, nonce [8]byte, err error) {
	if _, err = rand.Read(nonce[:]); err != nil {
		return nil, nonce, err
	}
	pkt = make([]byte, HandshakeReqPSKLen)
	pkt[0] = header(TypeHandshakeReq)
	pkt[hsOffMode] = AuthModePSK
	copy(pkt[hsOffNonce:hsOffTime], nonce[:])
	binary.BigEndian.PutUint64(pkt[hsOffTime:hsOffClientID], uint64(now.Unix()))
	copy(pkt[hsOffClientID:hsOffAuthStart], clientID[:])
	copy(pkt[hsOffAuthStart:], sign(psk, pkt[:hsOffAuthStart]))
	return pkt, nonce, nil
}

// BuildHandshakeReqToken builds a token-mode HandshakeReq, signed with the device key.
//
// The device public key is not a field: it is inside the token, where the licence server put it.
// That is what stops a stolen token being useful on its own - whoever presents it must also hold
// the matching private key.
func BuildHandshakeReqToken(deviceKey *ecdsa.PrivateKey, token []byte, clientID ClientID,
	now time.Time) (pkt []byte, nonce [8]byte, err error) {

	if len(token) != TokenLen {
		return nil, nonce, ErrBadTokenLength
	}
	if _, err = rand.Read(nonce[:]); err != nil {
		return nil, nonce, err
	}
	pkt = make([]byte, HandshakeReqTokenLen)
	pkt[0] = header(TypeHandshakeReq)
	pkt[hsOffMode] = AuthModeToken
	copy(pkt[hsOffNonce:hsOffTime], nonce[:])
	binary.BigEndian.PutUint64(pkt[hsOffTime:hsOffClientID], uint64(now.Unix()))
	copy(pkt[hsOffClientID:hsOffAuthStart], clientID[:])
	copy(pkt[hsOffAuthStart:hsTokenSigStart], token)

	sig, err := Sign(deviceKey, pkt[:hsTokenSigStart])
	if err != nil {
		return nil, nonce, err
	}
	copy(pkt[hsTokenSigStart:], sig)
	return pkt, nonce, nil
}

// HandshakeReqMode reads the auth mode without validating anything else, so a relay can route a
// packet to the right verifier - or drop it in silence when it is for the mode this relay does
// not serve.
func HandshakeReqMode(pkt []byte) (byte, error) {
	if len(pkt) < hsOffNonce {
		return 0, ErrShortPacket
	}
	v, t := ParseHeader(pkt[0])
	if v != Version {
		return 0, ErrBadVersion
	}
	if t != TypeHandshakeReq {
		return 0, ErrBadType
	}
	return pkt[hsOffMode], nil
}

// VerifyHandshakeReq checks a PSK-mode request: the HMAC first, then the clock skew.
func VerifyHandshakeReq(psk, pkt []byte, now time.Time) (ClientID, [8]byte, error) {
	var id ClientID
	var nonce [8]byte
	if len(pkt) != HandshakeReqPSKLen {
		return id, nonce, ErrShortPacket
	}
	v, t := ParseHeader(pkt[0])
	if v != Version {
		return id, nonce, ErrBadVersion
	}
	if t != TypeHandshakeReq {
		return id, nonce, ErrBadType
	}
	if pkt[hsOffMode] != AuthModePSK {
		return id, nonce, ErrBadAuthMode
	}
	if !hmac.Equal(pkt[hsOffAuthStart:], sign(psk, pkt[:hsOffAuthStart])) {
		return id, nonce, ErrBadAuth
	}
	ts := time.Unix(int64(binary.BigEndian.Uint64(pkt[hsOffTime:hsOffClientID])), 0)
	if d := now.Sub(ts); d > HandshakeSkew || d < -HandshakeSkew {
		return id, nonce, ErrClockSkew
	}
	copy(id[:], pkt[hsOffClientID:hsOffAuthStart])
	copy(nonce[:], pkt[hsOffNonce:hsOffTime])
	return id, nonce, nil
}

// VerifyHandshakeReqToken checks a token-mode request, in the only order that is safe:
//
//  1. the token's signature, against the licence public key
//  2. the token's expiry
//  3. the request's signature, against the device key the token carries
//  4. the clock skew
//
// Reading any field before step 1 would be trusting bytes an attacker chose. Step 3 has to come
// after step 1 because the key it uses comes out of the token.
//
// A token that verifies but has expired is returned WITH ErrTokenExpired, so the caller can tell
// a real customer why they were refused instead of dropping them in silence.
func VerifyHandshakeReqToken(licencePub *ecdsa.PublicKey, pkt []byte, now time.Time) (
	*Token, ClientID, [8]byte, error) {

	var id ClientID
	var nonce [8]byte
	if len(pkt) != HandshakeReqTokenLen {
		return nil, id, nonce, ErrShortPacket
	}
	v, t := ParseHeader(pkt[0])
	if v != Version {
		return nil, id, nonce, ErrBadVersion
	}
	if t != TypeHandshakeReq {
		return nil, id, nonce, ErrBadType
	}
	if pkt[hsOffMode] != AuthModeToken {
		return nil, id, nonce, ErrBadAuthMode
	}

	tok, err := VerifyToken(licencePub, pkt[hsOffAuthStart:hsTokenSigStart], now)
	if err != nil && err != ErrTokenExpired {
		return nil, id, nonce, err
	}
	expired := err == ErrTokenExpired

	if !Verify(tok.DeviceKey, pkt[:hsTokenSigStart], pkt[hsTokenSigStart:]) {
		return nil, id, nonce, ErrBadAuth
	}

	ts := time.Unix(int64(binary.BigEndian.Uint64(pkt[hsOffTime:hsOffClientID])), 0)
	if d := now.Sub(ts); d > HandshakeSkew || d < -HandshakeSkew {
		return nil, id, nonce, ErrClockSkew
	}

	copy(id[:], pkt[hsOffClientID:hsOffAuthStart])
	copy(nonce[:], pkt[hsOffNonce:hsOffTime])
	if expired {
		return tok, id, nonce, ErrTokenExpired
	}
	return tok, id, nonce, nil
}

// BuildVersionMismatchResp answers a handshake from a client speaking a different protocol
// version. The reply deliberately carries the CLIENT's version in its header, not ours: a client
// that cannot parse the answer learns nothing, and silence is indistinguishable from a dead
// relay or a blocked port.
//
// It emits the V2 layout, not v3's. A v1 or v2 client can only parse what it already knows, and
// handing it a longer packet with a nonce echo it has never heard of turns "please update" back
// into the four-attempt timeout this exists to avoid.
//
// This only works in PSK mode. A token-mode relay shares no key with the old client, so anything
// it sent would fail that client's HMAC check anyway; there it stays silent.
func BuildVersionMismatchResp(psk []byte, clientVersion byte) []byte {
	pkt := make([]byte, HandshakeRespV2Len)
	pkt[0] = clientVersion<<4 | TypeHandshakeResp
	pkt[1] = StatusVersionMismatch
	copy(pkt[20:], sign(psk, pkt[:20]))
	return pkt
}

// Response layout, common to both modes:
//
//	off  len  field
//	0    1    header
//	1    1    status
//	2    8    session id
//	10   4    inner client IPv4
//	14   4    inner relay IPv4
//	18   2    MTU
//	20   8    echo of the client nonce
//	28   ..   HMAC (32) in PSK mode, relay signature (64) in token mode
const hsRespAuthStart = 28

func fillHandshakeResp(pkt []byte, status byte, sid SessionID, clientIP, relayIP netip.Addr,
	mtu uint16, nonce [8]byte) {

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
	// The nonce echo binds this answer to one request. Without it a captured response can be
	// replayed at a client that is mid-handshake, handing it a session id the relay has already
	// forgotten - a silent blackhole until the idle timeout. v2 had this hole.
	copy(pkt[20:hsRespAuthStart], nonce[:])
}

// BuildHandshakeResp packs a PSK-mode result and signs it with the shared key.
func BuildHandshakeResp(psk []byte, status byte, sid SessionID, clientIP, relayIP netip.Addr,
	mtu uint16, nonce [8]byte) []byte {

	pkt := make([]byte, HandshakeRespPSKLen)
	fillHandshakeResp(pkt, status, sid, clientIP, relayIP, mtu, nonce)
	copy(pkt[hsRespAuthStart:], sign(psk, pkt[:hsRespAuthStart]))
	return pkt
}

// BuildHandshakeRespToken packs a token-mode result and signs it with the RELAY's own key.
//
// In token mode there is no shared secret, so the relay signs with a key of its own. Its public
// half travels in the profile, beside its endpoint, which the client fetches over HTTPS from an
// authenticated endpoint. This is new in v3: under v2 a client could only tell a real relay from
// a forged answer because both sides held the same key.
func BuildHandshakeRespToken(relayPriv *ecdsa.PrivateKey, status byte, sid SessionID,
	clientIP, relayIP netip.Addr, mtu uint16, nonce [8]byte) ([]byte, error) {

	pkt := make([]byte, HandshakeRespTokenLen)
	fillHandshakeResp(pkt, status, sid, clientIP, relayIP, mtu, nonce)
	sig, err := Sign(relayPriv, pkt[:hsRespAuthStart])
	if err != nil {
		return nil, err
	}
	copy(pkt[hsRespAuthStart:], sig)
	return pkt, nil
}

// HandshakeResult is the content of a HandshakeResp once the client has verified it.
type HandshakeResult struct {
	Status   byte
	Session  SessionID
	ClientIP netip.Addr
	RelayIP  netip.Addr
	MTU      uint16
}

func readHandshakeResp(pkt []byte) HandshakeResult {
	var r HandshakeResult
	r.Status = pkt[1]
	copy(r.Session[:], pkt[2:10])
	r.ClientIP = netip.AddrFrom4([4]byte(pkt[10:14]))
	r.RelayIP = netip.AddrFrom4([4]byte(pkt[14:18]))
	r.MTU = binary.BigEndian.Uint16(pkt[18:20])
	return r
}

// ParseHandshakeResp verifies a PSK-mode answer and checks the nonce echo against the one this
// client sent.
func ParseHandshakeResp(psk, pkt []byte, nonce [8]byte) (HandshakeResult, error) {
	var r HandshakeResult
	if len(pkt) != HandshakeRespPSKLen {
		return r, ErrShortPacket
	}
	if v, t := ParseHeader(pkt[0]); v != Version {
		return r, ErrBadVersion
	} else if t != TypeHandshakeResp {
		return r, ErrBadType
	}
	if !hmac.Equal(pkt[hsRespAuthStart:], sign(psk, pkt[:hsRespAuthStart])) {
		return r, ErrBadAuth
	}
	if !hmac.Equal(pkt[20:hsRespAuthStart], nonce[:]) {
		return r, ErrBadNonce
	}
	return readHandshakeResp(pkt), nil
}

// ParseHandshakeRespToken verifies a token-mode answer against the relay's public key, which the
// client got from the profile, and checks the nonce echo.
func ParseHandshakeRespToken(relayPub *ecdsa.PublicKey, pkt []byte, nonce [8]byte) (HandshakeResult, error) {
	var r HandshakeResult
	if len(pkt) != HandshakeRespTokenLen {
		return r, ErrShortPacket
	}
	if v, t := ParseHeader(pkt[0]); v != Version {
		return r, ErrBadVersion
	} else if t != TypeHandshakeResp {
		return r, ErrBadType
	}
	if !Verify(relayPub, pkt[:hsRespAuthStart], pkt[hsRespAuthStart:]) {
		return r, ErrBadAuth
	}
	if !hmac.Equal(pkt[20:hsRespAuthStart], nonce[:]) {
		return r, ErrBadNonce
	}
	return readHandshakeResp(pkt), nil
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
