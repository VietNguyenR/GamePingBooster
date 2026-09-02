package protocol

// P-256 signing and verification, shared by the v3 handshake.
//
// Why P-256 and not Ed25519, which would be the obvious modern choice: this project's rule is
// standard library only on BOTH sides, and .NET 9 has no Ed25519 and no X25519. It does have
// ECDsa, ECDiffieHellman with DeriveRawSecretAgreement, HKDF and AesGcm. Choosing Ed25519 would
// mean a NuGet dependency in the client, which costs more than the ergonomic difference is
// worth. See docs/PROTOCOL-v3.md.
//
// Signatures are the raw 64-byte r||s pair, NOT ASN.1 DER. DER is variable length, which a
// fixed-layout wire format cannot use. Go produces r and s separately and this file pads them;
// .NET's ECDsa.SignHash and VerifyHash already speak exactly this format.

import (
	"crypto/ecdh"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"errors"
	"math/big"
)

const (
	// PublicKeyLen is an uncompressed P-256 point: 0x04 || X(32) || Y(32).
	PublicKeyLen = 65
	// SignatureLen is r || s, each padded to the 32-byte field size.
	SignatureLen = 64
	// PrivateKeyLen is the scalar d, big-endian.
	PrivateKeyLen = 32
)

var (
	ErrBadPublicKey  = errors.New("not a valid uncompressed P-256 public key")
	ErrBadPrivateKey = errors.New("not a valid P-256 private key")
	ErrBadSignature  = errors.New("signature is not 64 bytes")
)

// MarshalPublicKey encodes a public key as the 65-byte uncompressed form used on the wire.
func MarshalPublicKey(pub *ecdsa.PublicKey) []byte {
	out := make([]byte, PublicKeyLen)
	out[0] = 4
	pub.X.FillBytes(out[1:33])
	pub.Y.FillBytes(out[33:65])
	return out
}

// ParsePublicKey decodes the 65-byte uncompressed form.
//
// The point is validated by handing it to crypto/ecdh, which rejects anything not on the curve.
// A hand-rolled parser would accept an off-curve point happily and then verify signatures
// against it, which is a real attack and not a theoretical one. elliptic.Unmarshal and
// IsOnCurve would do the same job but are both deprecated.
func ParsePublicKey(raw []byte) (*ecdsa.PublicKey, error) {
	if len(raw) != PublicKeyLen || raw[0] != 4 {
		return nil, ErrBadPublicKey
	}
	if _, err := ecdh.P256().NewPublicKey(raw); err != nil {
		return nil, ErrBadPublicKey
	}
	return &ecdsa.PublicKey{
		Curve: elliptic.P256(),
		X:     new(big.Int).SetBytes(raw[1:33]),
		Y:     new(big.Int).SetBytes(raw[33:65]),
	}, nil
}

// ParsePrivateKey rebuilds a key from the 32-byte scalar, deriving the public half.
func ParsePrivateKey(raw []byte) (*ecdsa.PrivateKey, error) {
	if len(raw) != PrivateKeyLen {
		return nil, ErrBadPrivateKey
	}
	// ecdh validates the scalar is in range; a zero or out-of-range d must not become a key.
	if _, err := ecdh.P256().NewPrivateKey(raw); err != nil {
		return nil, ErrBadPrivateKey
	}
	d := new(big.Int).SetBytes(raw)
	priv := &ecdsa.PrivateKey{D: d}
	priv.PublicKey.Curve = elliptic.P256()
	priv.PublicKey.X, priv.PublicKey.Y = elliptic.P256().ScalarBaseMult(raw)
	return priv, nil
}

// GenerateKey mints a new P-256 key pair.
func GenerateKey() (*ecdsa.PrivateKey, error) {
	return ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
}

// Sign returns the raw 64-byte r||s signature over SHA-256 of msg.
func Sign(priv *ecdsa.PrivateKey, msg []byte) ([]byte, error) {
	sum := sha256.Sum256(msg)
	r, s, err := ecdsa.Sign(rand.Reader, priv, sum[:])
	if err != nil {
		return nil, err
	}
	sig := make([]byte, SignatureLen)
	r.FillBytes(sig[:32])
	s.FillBytes(sig[32:])
	return sig, nil
}

// Verify checks a raw 64-byte r||s signature over SHA-256 of msg.
func Verify(pub *ecdsa.PublicKey, msg, sig []byte) bool {
	if len(sig) != SignatureLen {
		return false
	}
	sum := sha256.Sum256(msg)
	r := new(big.Int).SetBytes(sig[:32])
	s := new(big.Int).SetBytes(sig[32:])
	return ecdsa.Verify(pub, sum[:], r, s)
}
