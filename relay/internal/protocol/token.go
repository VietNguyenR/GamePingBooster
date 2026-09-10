package protocol

// Tac gia Claude nhe deo phai tao nen la code co thoi thi chui no dung chui tao 🙏
// The licence token: what a paying client presents instead of a shared key.
//
// The relay verifies it OFFLINE, against a public key it already has. It makes no network call,
// keeps no user database, and holds no secret - so a relay can be rented, rebuilt or lost
// without leaking anything that lets somebody forge access. That property is the whole reason
// this is a signed blob rather than an API call.
//
// It is minted by the licence server and opaque to the client, which only stores it and hands it
// over at handshake time.
//
// Format changes here are the same five-place problem as the rest of the wire format, plus one:
// the licence server signs these, and it is a React Router application, so a third
// implementation exists in JavaScript. Node's crypto can produce the raw r||s form with
// dsaEncoding 'ieee-p1363'; anything else will not verify here.

import (
	"crypto/ecdsa"
	"encoding/binary"
	"errors"
	"time"
)

// TokenLen is fixed. See docs/PROTOCOL-v3.md.
const TokenLen = 150

// Field offsets inside a token.
const (
	tokenOffVersion   = 0
	tokenOffUserID    = 1
	tokenOffDeviceKey = 9
	tokenOffExpiry    = 74
	tokenOffTier      = 82
	tokenOffMaxSess   = 83
	tokenOffReserved  = 84
	tokenOffSignature = 86
)

// TokenVersion is the token's own format version, independent of the protocol version: the
// licence server may need to change what it puts in a token without a new handshake.
const TokenVersion = 1

var (
	ErrBadTokenLength  = errors.New("licence token is not 150 bytes")
	ErrBadTokenVersion = errors.New("unknown licence token version")
	ErrBadTokenFormat  = errors.New("licence token is malformed")
	ErrBadTokenSig     = errors.New("licence token signature does not verify")
	ErrTokenExpired    = errors.New("licence token has expired")
)

// Token is a verified licence token. It is only ever produced by VerifyToken, so holding one
// means the signature checked out - there is no way to construct an unverified Token from the
// wire and mistake it for a real one.
type Token struct {
	UserID    uint64
	DeviceKey *ecdsa.PublicKey
	Expiry    time.Time

	// Tier is the customer's plan, 0-255, as the licence server sees it. The relay compares it
	// against its own -min-tier and refuses a token that falls short; it knows nothing else about
	// plans, and deliberately holds no table of them. A tier this build has never heard of is
	// therefore not an error - a number is a number, and a new plan needs no relay deploy.
	Tier byte

	// MaxSess is the plan's concurrent-session allowance, and NOTHING ENFORCES IT.
	//
	// Said plainly because a signed field reads like a guarantee. The licence server writes it,
	// this package parses it, and no caller has ever looked at it: one device key can open as
	// many sessions at once as it likes on any relay. The limit that does bite is deviceLimit,
	// applied when a token is signed, and the relay never sees that one at all.
	//
	// It is kept rather than removed. Removing a field means a token layout change, which means
	// every relay in the fleet has to be redeployed before the licence server can mint one - an
	// expensive move for a business rule nobody has asked for. Leaving the byte reserves the
	// option to enforce it later at no cost. If concurrency needs limiting before then, the
	// twenty-second status report already carries user_id and device_key for every live session,
	// so the licence server can see it without the relay changing at all.
	MaxSess byte

	// deviceKeyRaw is kept so the relay can key an address reservation on the device rather
	// than on the client id, which a client chooses for itself and could otherwise use to take
	// over somebody else's reserved address.
	deviceKeyRaw [PublicKeyLen]byte
}

// DeviceKeyRaw returns the 65-byte encoding of the device public key.
func (t *Token) DeviceKeyRaw() []byte {
	out := make([]byte, PublicKeyLen)
	copy(out, t.deviceKeyRaw[:])
	return out
}

// BuildToken mints and signs a token. The relay never calls this - the licence server does, and
// in production that is the React Router service, not this package. It exists here so the
// verification path can be tested against something other than its own output.
func BuildToken(licencePriv *ecdsa.PrivateKey, userID uint64, deviceKey []byte,
	expiry time.Time, tier, maxSess byte) ([]byte, error) {

	if len(deviceKey) != PublicKeyLen {
		return nil, ErrBadPublicKey
	}
	if _, err := ParsePublicKey(deviceKey); err != nil {
		return nil, err
	}

	tok := make([]byte, TokenLen)
	tok[tokenOffVersion] = TokenVersion
	binary.BigEndian.PutUint64(tok[tokenOffUserID:tokenOffDeviceKey], userID)
	copy(tok[tokenOffDeviceKey:tokenOffExpiry], deviceKey)
	binary.BigEndian.PutUint64(tok[tokenOffExpiry:tokenOffTier], uint64(expiry.Unix()))
	tok[tokenOffTier] = tier
	tok[tokenOffMaxSess] = maxSess
	// bytes 84..86 stay zero

	sig, err := Sign(licencePriv, tok[:tokenOffSignature])
	if err != nil {
		return nil, err
	}
	copy(tok[tokenOffSignature:], sig)
	return tok, nil
}

// VerifyToken checks a token's signature and expiry against the licence public key.
//
// Expiry is checked here rather than left to the caller, because a caller that forgets is the
// difference between a subscription that lapses and one that never does.
func VerifyToken(licencePub *ecdsa.PublicKey, tok []byte, now time.Time) (*Token, error) {
	if len(tok) != TokenLen {
		return nil, ErrBadTokenLength
	}
	if tok[tokenOffVersion] != TokenVersion {
		return nil, ErrBadTokenVersion
	}
	// Reserved bytes must be zero. Accepting anything there would let a future field be
	// smuggled past a relay that predates it.
	if tok[tokenOffReserved] != 0 || tok[tokenOffReserved+1] != 0 {
		return nil, ErrBadTokenFormat
	}

	// Signature first: nothing in the token means anything until it verifies. Reading the
	// expiry or the device key before this would be trusting attacker-supplied bytes.
	if !Verify(licencePub, tok[:tokenOffSignature], tok[tokenOffSignature:]) {
		return nil, ErrBadTokenSig
	}

	deviceKey, err := ParsePublicKey(tok[tokenOffDeviceKey:tokenOffExpiry])
	if err != nil {
		return nil, err
	}

	t := &Token{
		UserID:    binary.BigEndian.Uint64(tok[tokenOffUserID:tokenOffDeviceKey]),
		DeviceKey: deviceKey,
		Expiry:    time.Unix(int64(binary.BigEndian.Uint64(tok[tokenOffExpiry:tokenOffTier])), 0),
		Tier:      tok[tokenOffTier],
		MaxSess:   tok[tokenOffMaxSess],
	}
	copy(t.deviceKeyRaw[:], tok[tokenOffDeviceKey:tokenOffExpiry])

	if now.After(t.Expiry) {
		return t, ErrTokenExpired
	}
	return t, nil
}
