package main

// Key loading had no tests at all until internal/keyfile was extracted out from under it. These
// cover the two properties whose failure would be worst and least obvious from the relay's own
// logs.

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/gamepingbooster/relay/internal/keyfile"
	"github.com/gamepingbooster/relay/internal/protocol"
)

func TestLoadLicenceKeyAcceptsWhatLicenceGenWrites(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "licence.pub")

	key, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	// Written exactly the way licence-gen keygen writes it - same function, same encoding.
	if err := keyfile.WriteHex(path, protocol.MarshalPublicKey(&key.PublicKey), 0o644); err != nil {
		t.Fatal(err)
	}

	got, err := loadLicenceKey(path)
	if err != nil {
		t.Fatalf("relayd cannot load the key licence-gen just wrote: %v", err)
	}
	if got.X.Cmp(key.X) != 0 || got.Y.Cmp(key.Y) != 0 {
		t.Fatal("loaded a different key than was written")
	}
}

func TestLoadLicenceKeyRejectsAPrivateKey(t *testing.T) {
	// -licence-key wants the PUBLIC half. Handing it the private one is a plausible slip, and
	// the 32 versus 65 byte check is the only thing that catches it.
	dir := t.TempDir()
	path := filepath.Join(dir, "wrong.key")

	key, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	d := make([]byte, protocol.PrivateKeyLen)
	key.D.FillBytes(d)
	if err := keyfile.WriteHex(path, d, 0o600); err != nil {
		t.Fatal(err)
	}

	_, err = loadLicenceKey(path)
	if err == nil {
		t.Fatal("relayd accepted a private key where a public key belongs")
	}
	if !strings.Contains(err.Error(), "want 65") {
		t.Fatalf("the error does not say what shape was expected: %v", err)
	}
}

func TestRelayKeyIsStableAcrossRestarts(t *testing.T) {
	// The property the comment in keys.go warns about: a relay that minted a fresh key on every
	// start would invalidate the public key already published in the profile, and every client
	// would begin refusing its handshake answers. That surfaces as a network problem and is
	// nothing of the sort, so it is worth a test rather than a comment alone.
	dir := t.TempDir()
	path := filepath.Join(dir, "relay.key")

	first, created, err := loadOrCreateRelayKey(path)
	if err != nil {
		t.Fatalf("first start: %v", err)
	}
	if !created {
		t.Fatal("the first start did not report creating a key")
	}

	second, created, err := loadOrCreateRelayKey(path)
	if err != nil {
		t.Fatalf("second start: %v", err)
	}
	if created {
		t.Fatal("the second start created a NEW key; every published public key would be stale")
	}
	if first.D.Cmp(second.D) != 0 {
		t.Fatal("the relay came back with a different private key")
	}
}

func TestRelayKeyToleratesAPastedFile(t *testing.T) {
	// Trailing newline, an 0x prefix and stray spaces all arrive from a copy and paste sooner or
	// later, and none of them is visible to the person making the mistake.
	dir := t.TempDir()

	key, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	d := make([]byte, protocol.PrivateKeyLen)
	key.D.FillBytes(d)

	cleanPath := filepath.Join(dir, "clean.key")
	if err := keyfile.WriteHex(cleanPath, d, 0o600); err != nil {
		t.Fatal(err)
	}
	raw, err := os.ReadFile(cleanPath)
	if err != nil {
		t.Fatal(err)
	}
	clean := strings.TrimSpace(string(raw))

	messy := "0x" + clean[:8] + " " + clean[8:] + "\n\n"
	messyPath := filepath.Join(dir, "messy.key")
	if err := os.WriteFile(messyPath, []byte(messy), 0o600); err != nil {
		t.Fatal(err)
	}

	got, _, err := loadOrCreateRelayKey(messyPath)
	if err != nil {
		t.Fatalf("a pasted key file was rejected: %v", err)
	}
	if got.D.Cmp(key.D) != 0 {
		t.Fatal("the pasted file loaded as a different key")
	}
}
