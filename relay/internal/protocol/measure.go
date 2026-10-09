package protocol

// The measurement ticket: how a client times every way into a relay without holding a session there.
//
// The relay list in the app has to show the ping the player will actually get, through whichever way
// in (the relay itself or an entry in front of it) a connect would pick. Only a UDP round trip through
// relayd measures that. A Probe does it, but a Probe needs a live session id, and a session is a slot
// under -max-clients: a list that handshook every relay whenever it opened would take a slot on every
// relay for every player looking at it, and at peak the players trying to connect would be refused.
//
// So a ticket instead. The client sends an ordinary token-mode HandshakeReq with the mode byte set to
// AuthModeMeasure; the relay verifies it exactly as it would a handshake and answers with a ticket - a
// short expiry and a device tag under a MAC only the relay can make - and allocates nothing. Probes
// carrying the ticket are answered by checking the MAC. The relay keeps no state per ticket beyond a
// rate limit, so a thousand players with the list open cost it no slot at all.
//
// The ticket key is derived from the relay's own private key rather than drawn at start, so a ticket
// survives a hot restart (and a cold one) without the handoff having to carry it.
//
// Layouts, all TypeMeasure, op at offset 1:
//
//	MeasureTicket  (relay -> client, 42 bytes)
//	  off len field
//	  0   1   header
//	  1   1   op = MeasureOpTicket
//	  2   8   echo of the HandshakeReq's nonce
//	  10  32  ticket
//
//	MeasureProbe / MeasureReply  (42 bytes each way)
//	  0   1   header
//	  1   1   op = MeasureOpProbe, or MeasureOpReply on the way back
//	  2   8   stamp, opaque to the relay and echoed unchanged
//	  10  32  ticket, echoed unchanged
//
//	ticket (32 bytes)
//	  0   8   expiry, unix seconds, big-endian
//	  8   8   device tag: the first 8 bytes of SHA-256 over the 65-byte device key
//	  16  16  HMAC-SHA256 over bytes 0..16 under the relay's ticket key, truncated
//
// The ticket answer is not signed. It is bound to its request by the nonce echo, and a forged one
// could do nothing but fail to work: the relay is the only party that checks a ticket.

import (
	"crypto/ecdsa"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/binary"
	"time"
)

const (
	MeasureOpTicket = 1
	MeasureOpProbe  = 2
	MeasureOpReply  = 3

	// MeasureLen is every TypeMeasure message. A reply is exactly its probe's size, so the relay
	// amplifies nothing.
	MeasureLen = 42
	TicketLen  = 32

	// TicketLifetime is how long a ticket is good for, at most - never past the token it came from.
	// Long enough that an open list asks for one now and then rather than all the time, short enough
	// that a revoked or lapsed licence stops measuring within minutes.
	TicketLifetime = 10 * time.Minute
)

const (
	msOffOp     = 1
	msOffStamp  = 2 // the nonce in a ticket answer, the stamp in a probe
	msOffTicket = 10

	tkOffExpiry = 0
	tkOffDevice = 8
	tkOffMAC    = 16
)

// TicketKey derives the key tickets are made with from the relay's own private key. Deterministic, so
// every relayd started with the same key file - after a hot or cold restart alike - accepts the
// tickets the last one issued.
func TicketKey(relayPriv *ecdsa.PrivateKey) []byte {
	h := sha256.New()
	h.Write([]byte("gpb measure ticket v1"))
	h.Write(relayPriv.D.FillBytes(make([]byte, 32)))
	return h.Sum(nil)
}

// DeviceTag is the eight bytes a ticket names its device by.
func DeviceTag(deviceKey []byte) [8]byte {
	sum := sha256.Sum256(deviceKey)
	var tag [8]byte
	copy(tag[:], sum[:8])
	return tag
}

func ticketMAC(key, body []byte) []byte {
	m := hmac.New(sha256.New, key)
	m.Write(body)
	return m.Sum(nil)[:TicketLen-tkOffMAC]
}

// MintTicket makes a ticket for a device, good until expiry.
func MintTicket(key []byte, device [8]byte, expiry time.Time) [TicketLen]byte {
	var t [TicketLen]byte
	binary.BigEndian.PutUint64(t[tkOffExpiry:tkOffDevice], uint64(expiry.Unix()))
	copy(t[tkOffDevice:tkOffMAC], device[:])
	copy(t[tkOffMAC:], ticketMAC(key, t[:tkOffMAC]))
	return t
}

// CheckTicket returns the ticket's device tag when its MAC verifies and it has not expired. The MAC is
// checked first: nothing in a ticket means anything until it does.
func CheckTicket(key, ticket []byte, now time.Time) ([8]byte, bool) {
	var tag [8]byte
	if len(ticket) != TicketLen || !hmac.Equal(ticket[tkOffMAC:], ticketMAC(key, ticket[:tkOffMAC])) {
		return tag, false
	}
	if now.Unix() > int64(binary.BigEndian.Uint64(ticket[tkOffExpiry:tkOffDevice])) {
		return tag, false
	}
	copy(tag[:], ticket[tkOffDevice:tkOffMAC])
	return tag, true
}

// BuildMeasureReq builds the request for a ticket: a token HandshakeReq in AuthModeMeasure.
func BuildMeasureReq(deviceKey *ecdsa.PrivateKey, token []byte, clientID ClientID,
	now time.Time) (pkt []byte, nonce [8]byte, err error) {
	return buildTokenReq(deviceKey, token, clientID, now, AuthModeMeasure)
}

// VerifyMeasureReq checks a ticket request exactly as VerifyHandshakeReqToken checks a handshake, and
// returns ErrTokenExpired with the token in the same way.
func VerifyMeasureReq(licencePub *ecdsa.PublicKey, pkt []byte, now time.Time) (
	*Token, ClientID, [8]byte, error) {
	return verifyTokenReq(licencePub, pkt, now, AuthModeMeasure)
}

func buildMeasure(op byte, field uint64, ticket []byte) []byte {
	pkt := make([]byte, MeasureLen)
	pkt[0] = header(TypeMeasure)
	pkt[msOffOp] = op
	binary.BigEndian.PutUint64(pkt[msOffStamp:msOffTicket], field)
	copy(pkt[msOffTicket:], ticket)
	return pkt
}

// BuildMeasureTicket is the relay's answer to a ticket request.
func BuildMeasureTicket(nonce [8]byte, ticket [TicketLen]byte) []byte {
	return buildMeasure(MeasureOpTicket, binary.BigEndian.Uint64(nonce[:]), ticket[:])
}

// BuildMeasureProbe is one timed probe carrying a ticket.
func BuildMeasureProbe(ticket []byte, stamp uint64) []byte {
	return buildMeasure(MeasureOpProbe, stamp, ticket)
}

// ParseMeasure reads any TypeMeasure message: its op, its stamp (the nonce for a ticket answer), and
// its ticket, which aliases pkt.
func ParseMeasure(pkt []byte) (op byte, stamp uint64, ticket []byte, err error) {
	if len(pkt) != MeasureLen {
		return 0, 0, nil, ErrShortPacket
	}
	if v, t := ParseHeader(pkt[0]); v != Version {
		return 0, 0, nil, ErrBadVersion
	} else if t != TypeMeasure {
		return 0, 0, nil, ErrBadType
	}
	return pkt[msOffOp], binary.BigEndian.Uint64(pkt[msOffStamp:msOffTicket]), pkt[msOffTicket:], nil
}

// MeasureReplyInto turns a probe into its reply in place: the op byte is the only difference.
func MeasureReplyInto(pkt []byte) { pkt[msOffOp] = MeasureOpReply }
