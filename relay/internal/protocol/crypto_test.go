package protocol

import (
	"bytes"
	"crypto/elliptic"
	"encoding/hex"
	"math/big"
	"testing"
)

func TestP256RoundTrip(t *testing.T) {
	key, err := GenerateKey()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	msg := []byte("the quick brown fox")

	sig, err := Sign(key, msg)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	if len(sig) != SignatureLen {
		t.Fatalf("signature is %d bytes, want %d", len(sig), SignatureLen)
	}
	if !Verify(&key.PublicKey, msg, sig) {
		t.Fatal("a freshly made signature did not verify")
	}
}

// A signature must ALWAYS be exactly 64 bytes, even when r or s happens to be small.
//
// This is the classic way raw ECDSA encoding goes wrong: r is a number, and roughly one time in
// 256 it is below 2^248 and its natural big-endian encoding is 31 bytes or fewer. Code that
// writes r.Bytes() straight out produces a short signature that the other side cannot parse, and
// it does so on about 0.4% of connections - rare enough to reach production, common enough to
// affect somebody every day.
//
// One signature would almost never catch it. 2000 makes it a near certainty, and the test also
// counts how many short values it actually exercised so a future reader can see it is doing
// something rather than passing vacuously.
func TestP256SignatureIsAlwaysSixtyFourBytes(t *testing.T) {
	key, err := GenerateKey()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	msg := []byte("padding matters")

	shortHalves := 0
	const rounds = 2000
	for i := 0; i < rounds; i++ {
		sig, err := Sign(key, msg)
		if err != nil {
			t.Fatalf("sign %d: %v", i, err)
		}
		if len(sig) != SignatureLen {
			t.Fatalf("round %d: signature is %d bytes, want %d", i, len(sig), SignatureLen)
		}
		if !Verify(&key.PublicKey, msg, sig) {
			t.Fatalf("round %d: signature did not verify", i)
		}
		// A leading zero byte means the number needed padding to reach 32 bytes.
		if sig[0] == 0 {
			shortHalves++
		}
		if sig[32] == 0 {
			shortHalves++
		}
	}
	if shortHalves == 0 {
		t.Logf("warning: %d signatures and not one short half - the padding path went untested", rounds)
	} else {
		t.Logf("%d of %d signature halves needed padding", shortHalves, rounds*2)
	}
}

func TestP256VerifyRejectsTampering(t *testing.T) {
	key, err := GenerateKey()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	msg := []byte("original message")
	sig, err := Sign(key, msg)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}

	if Verify(&key.PublicKey, []byte("original messagf"), sig) {
		t.Error("a changed message still verified")
	}

	bad := bytes.Clone(sig)
	bad[0] ^= 1
	if Verify(&key.PublicKey, msg, bad) {
		t.Error("a changed r still verified")
	}

	bad = bytes.Clone(sig)
	bad[63] ^= 1
	if Verify(&key.PublicKey, msg, bad) {
		t.Error("a changed s still verified")
	}

	if Verify(&key.PublicKey, msg, sig[:63]) {
		t.Error("a 63-byte signature still verified")
	}

	other, err := GenerateKey()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if Verify(&other.PublicKey, msg, sig) {
		t.Error("another key's signature still verified")
	}
}

func TestP256PublicKeyEncoding(t *testing.T) {
	key, err := GenerateKey()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	raw := MarshalPublicKey(&key.PublicKey)
	if len(raw) != PublicKeyLen {
		t.Fatalf("marshalled key is %d bytes, want %d", len(raw), PublicKeyLen)
	}
	if raw[0] != 4 {
		t.Fatalf("first byte is 0x%02x, want 0x04 (uncompressed)", raw[0])
	}

	back, err := ParsePublicKey(raw)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if back.X.Cmp(key.PublicKey.X) != 0 || back.Y.Cmp(key.PublicKey.Y) != 0 {
		t.Fatal("the key did not survive a marshal/parse round trip")
	}
}

// An off-curve point must be refused. A parser that only checks the length and the 0x04 prefix
// accepts it, and then verifies signatures against a point that is not on P-256 at all - which
// is an actual attack, not a theoretical one.
func TestP256ParsePublicKeyRejectsGarbage(t *testing.T) {
	key, err := GenerateKey()
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	good := MarshalPublicKey(&key.PublicKey)

	offCurve := bytes.Clone(good)
	offCurve[64] ^= 1 // same X, a Y that is not its pair
	if x := new(big.Int).SetBytes(offCurve[1:33]); x.Sign() == 0 {
		t.Skip("degenerate key, rerun")
	}

	cases := map[string][]byte{
		"empty":             {},
		"too short":         good[:64],
		"too long":          append(bytes.Clone(good), 0),
		"compressed form":   append([]byte{2}, good[1:33]...),
		"wrong prefix":      append([]byte{5}, good[1:]...),
		"off the curve":     offCurve,
		"all zero":          make([]byte, PublicKeyLen),
		"point at infinity": func() []byte { b := make([]byte, PublicKeyLen); b[0] = 4; return b }(),
	}
	for name, raw := range cases {
		if _, err := ParsePublicKey(raw); err == nil {
			t.Errorf("%s: accepted, want rejected", name)
		}
	}
}

func TestP256ParsePrivateKeyRejectsGarbage(t *testing.T) {
	order := elliptic.P256().Params().N

	cases := map[string][]byte{
		"empty":     {},
		"too short": make([]byte, 31),
		"too long":  make([]byte, 33),
		"zero":      make([]byte, 32),
		"the order": order.FillBytes(make([]byte, 32)),
	}
	for name, raw := range cases {
		if _, err := ParsePrivateKey(raw); err == nil {
			t.Errorf("%s: accepted, want rejected", name)
		}
	}
}

// The scalar has to produce the public half the vectors were built with, or every cross-language
// check downstream is comparing against the wrong key.
func TestP256ParsePrivateKeyDerivesTheRightPublicKey(t *testing.T) {
	d, err := hex.DecodeString(vectorP256PrivHex)
	if err != nil {
		t.Fatalf("bad test constant: %v", err)
	}
	key, err := ParsePrivateKey(d)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	got := hex.EncodeToString(MarshalPublicKey(&key.PublicKey))
	if got != vectorP256PubHex {
		t.Errorf("derived public key\n got %s\nwant %s", got, vectorP256PubHex)
	}
}
